using System.Collections.Concurrent;
using DistributedHls.Core;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

// One loop owns the in-memory scheduling state; every store transition is a conditional update inside SQLite, so nothing here locks.
// Event handlers only record what happened; the loop does all I/O.
public sealed class Coordinator(JobStore store, ILibraryManager library, PublishedIndex index, AccessPolicy policy, VersionCatalog catalog,
    ILogger<Coordinator> logger) : BackgroundService
{
    private const int UpsertBatch = 500;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    // Arrays sorted, so a reordered list is no change.
    private sealed record ConfigSnapshot(bool Enabled, Guid[] Libraries, int[] Qualities, string OutputPath);

    // Item id → still exists.
    private readonly ConcurrentDictionary<Guid, bool> _changes = new();
    private readonly ConcurrentDictionary<string, byte> _toSchedule = new();
    private int _configVersion;
    private int _appliedVersion;
    private int _rebuild;
    private ConfigSnapshot? _last;
    private DateTimeOffset _nextSweep;
    private DateTimeOffset _nextOrphanRun;

    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(Plugin.Instance.Configuration.LeaseMinutes);

    // A rebuild cancels unclaimed jobs, resets failed renditions and rescans; published outputs are kept.
    public void RequestRebuild() => Volatile.Write(ref _rebuild, 1);

    public void EnqueueSchedule(string sha) => _toSchedule[sha] = 1;

    // An item the worker knows before the library sync has reached it.
    public void EnqueueItem(Guid itemId) => _changes[itemId] = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hands control back to the host at once: the first pass reconciles the whole library.
        await Task.Yield();
        try
        {
            library.ItemAdded += OnItemChanged;
            library.ItemUpdated += OnItemChanged;
            library.ItemRemoved += OnItemRemoved;
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
            try
            {
                await CleanupLegacyAsync(stoppingToken);
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                do await TickAsync(stoppingToken);
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            finally
            {
                library.ItemAdded -= OnItemChanged;
                library.ItemUpdated -= OnItemChanged;
                library.ItemRemoved -= OnItemRemoved;
                Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
            }
        }
        // An exception leaving a BackgroundService stops the whole server.
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical(ex, "HLS 协调器意外停止。");
        }
    }

    public bool IsCandidate(Video item, out Guid libraryId)
    {
        libraryId = library.GetCollectionFolders(item).FirstOrDefault()?.Id ?? Guid.Empty;
        return !VersionCatalog.IsGenerated(item) && item.IsFileProtocol && !item.IsShortcut && libraryId != Guid.Empty && policy.LibraryEligible(libraryId);
    }

    // Also called by the worker API right after a probe or a directed poll.
    public void ScheduleSha(string sha, bool includeUnreferenced = false)
    {
        if (store.GetMedia(sha) is null) return;
        // Media no library file references is only encoded for a worker that asks for it by its hash (a directed poll),
        // because a normal poll could never claim that job.
        if (!includeUnreferenced && store.SourcesBySha(sha).Count == 0) return;
        if (!IsShaEligible(sha)) return;
        store.EnsureEncodeJob(sha, index.ItemsForSha(sha).Select(id => (Guid?)id).FirstOrDefault(), EncodingProfile.Version, DateTimeOffset.UtcNow);
    }

    public void SyncAndSchedule(string sha, bool includeUnreferenced = false)
    {
        SyncRenditions(sha, Plugin.Instance.Configuration.Qualities);
        ScheduleSha(sha, includeUnreferenced);
    }

    // Read from the store, not the index: Claim cancels a job it is told is not eligible.
    public bool IsShaEligible(string sha)
    {
        if (!Plugin.Instance.Configuration.Enabled) return false;
        var sources = store.SourcesBySha(sha);
        if (sources.Count > 0) return sources.Any(s => policy.LibraryEligible(s.File.LibraryId));
        // Media submitted by a worker without any library file.
        return store.GetMedia(sha) is { } media && policy.LibraryEligible(media.LibraryId);
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs args) => Track(args.Item, true);

    private void OnItemRemoved(object? sender, ItemChangeEventArgs args) => Track(args.Item, false);

    private void Track(BaseItem? item, bool exists)
    {
        if (item is not Video || VersionCatalog.IsGenerated(item)) return;
        _changes[item.Id] = exists;
        // A renamed or re-imaged original updates its version items; a removed one deletes them.
        if (index.GetSource(item.Id) is not null) catalog.MarkDirty([item.Id]);
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration configuration) => Interlocked.Increment(ref _configVersion);

    private async Task CleanupLegacyAsync(CancellationToken ct)
    {
        // Without the database there is nothing to clean up (PublishedIndex has logged why).
        if (store.InitError is not null) return;
        try
        {
            if (store.LegacyItems().Count > 0)
                logger.LogWarning("已从 v1 数据库迁移：v1 的输出目录不会自动删除，请手动清理 {OutputPath} 下的旧目录。", Plugin.Instance.Configuration.OutputPath);
            await catalog.CleanupLegacyAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "清理 v1 版本条目失败。");
        }
    }

    // Every step is isolated: a failure is only logged and never turns into a full rescan.
    private async Task TickAsync(CancellationToken ct)
    {
        // The plugin runs without data while the database is unavailable.
        if (store.InitError is not null) return;
        Step("应用配置", ApplyConfigurationIfChanged, ct);
        if (Plugin.Instance.Configuration.Enabled) Step("同步媒体库变更", () => ProcessLibraryChanges(UpsertBatch), ct);
        Step("回收过期租约", () => store.ReapExpiredLeases(Plugin.Instance.Configuration.MaxAttempts, LeaseDuration, DateTimeOffset.UtcNow), ct);
        Step("排程", () =>
        {
            foreach (var sha in _toSchedule.Keys)
                if (_toSchedule.TryRemove(sha, out _)) ScheduleIsolated(sha);
        }, ct);
        if (DateTimeOffset.UtcNow >= _nextSweep)
            Step("全量排程", () =>
            {
                foreach (var sha in store.ShasNeedingJobs(EncodingProfile.Version)) ScheduleIsolated(sha);
                _nextSweep = DateTimeOffset.UtcNow + Interval;
            }, ct);
        if (DateTimeOffset.UtcNow >= _nextOrphanRun) Step("清理孤儿媒体", CollectOrphans, ct);
        try
        {
            await catalog.ProcessDirtyAsync(200, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "HLS 协调步骤 {Step} 失败。", "同步版本条目");
        }
    }

    private void Step(string name, Action action, CancellationToken ct)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "HLS 协调步骤 {Step} 失败。", name);
        }
    }

    private void ScheduleIsolated(string sha)
    {
        try
        {
            ScheduleSha(sha);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "为 {Sha256} 排程失败。", sha);
        }
    }

    private void ApplyConfigurationIfChanged()
    {
        var version = Volatile.Read(ref _configVersion);
        var rebuild = Volatile.Read(ref _rebuild) == 1;
        if (_last is not null && version == _appliedVersion && !rebuild) return;
        var c = Plugin.Instance.Configuration;
        var next = new ConfigSnapshot(c.Enabled, [.. c.Libraries.Order()], [.. c.Qualities.Order()], c.OutputPath);
        var now = DateTimeOffset.UtcNow;
        if (!next.Enabled)
        {
            // Disabling empties the work list; published outputs stay. A rebuild requested meanwhile still resets failed renditions.
            store.CancelAllJobs(now);
            if (rebuild) store.ResetForRebuild(now);
            catalog.MarkAllDirty();
            Applied(next, version, rebuild);
            return;
        }
        var last = _last;
        // Also when just enabled: the disabled branch records qualities without applying them.
        if (last is null || rebuild || !last.Enabled || !last.Qualities.SequenceEqual(next.Qualities))
            foreach (var sha in store.AllMediaShas())
            {
                try
                {
                    SyncRenditions(sha, c.Qualities);
                    _toSchedule[sha] = 1;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "同步 {Sha256} 的档位失败。", sha);
                }
            }
        if (rebuild) store.ResetForRebuild(now);
        if (last is null || rebuild || !last.Enabled || !last.Libraries.SequenceEqual(next.Libraries)) FullScan();
        if (last is not null && last.OutputPath != next.OutputPath)
            logger.LogWarning("输出目录已从 {Old} 改为 {New}：已发布成果按相对路径在新目录下读取，迁移数据时请连同 .distributed-hls 标记文件一起复制；如果有意不迁移，请先在新目录中创建 .distributed-hls 文件，再执行“校验已发布成果”，缺失的档位会重新编码，旧目录需手动清理。", last.OutputPath, next.OutputPath);
        catalog.MarkAllDirty();
        Applied(next, version, rebuild);
    }

    private void Applied(ConfigSnapshot next, int version, bool rebuild)
    {
        _last = next;
        _appliedVersion = version;
        if (rebuild) Volatile.Write(ref _rebuild, 0);
    }

    private void SyncRenditions(string sha, IEnumerable<int> qualities)
    {
        if (store.GetMedia(sha) is not { } media) return;
        if (store.SyncRenditions(sha, RenditionPlanner.Desired(media.Probe, qualities), DateTimeOffset.UtcNow)) index.RefreshMedia([sha]);
    }

    private void FullScan()
    {
        var seen = new HashSet<Guid>();
        try
        {
            var batch = new List<SourceFile>(UpsertBatch);
            // Owned items and alternate versions too, so a scan sees every file a library event can report (local alternate
            // versions carry an OwnerId). Generated version items come back as well; IsCandidate leaves them out.
            var query = new InternalItemsQuery
            {
                MediaTypes = [MediaType.Video], Recursive = true, IsVirtualItem = false, IncludeOwnedItems = true, IncludeAlternateVersions = true
            };
            foreach (var video in library.GetItemList(query).OfType<Video>())
            {
                SourceFile? file;
                try
                {
                    file = IsCandidate(video, out var libraryId) ? ToSource(video, libraryId) : null;
                }
                catch (Exception ex)
                {
                    // Not evaluated is not gone: the stored source is kept.
                    logger.LogWarning(ex, "检查条目 {ItemId} 失败。", video.Id);
                    seen.Add(video.Id);
                    continue;
                }
                if (file is null) continue;
                seen.Add(file.ItemId);
                batch.Add(file);
                if (batch.Count < UpsertBatch) continue;
                Upsert(batch);
                batch.Clear();
            }
            if (batch.Count > 0) Upsert(batch);
            // Disabled during the scan: the later items failed IsCandidate for that reason only, and sources stay while disabled.
            if (!Plugin.Instance.Configuration.Enabled) return;
            foreach (var stale in store.Sources().Where(s => !seen.Contains(s.File.ItemId)))
                Isolated(stale.File.ItemId, () => RemoveSource(stale.File.ItemId));
        }
        finally
        {
            // Also after a failure: whatever was committed must reach the index.
            index.Load();
            catalog.MarkAllDirty();
        }
    }

    private void ProcessLibraryChanges(int max)
    {
        var processed = new List<Guid>();
        foreach (var itemId in _changes.Take(max).Select(p => p.Key).ToList())
        {
            // Disabled meanwhile: every item would fail IsCandidate and lose its source; the rest waits for the next enable.
            if (!Plugin.Instance.Configuration.Enabled) break;
            if (!_changes.TryRemove(itemId, out var exists)) continue;
            processed.Add(itemId);
            Isolated(itemId, () =>
            {
                if (exists && library.GetItemById(itemId) is Video video && IsCandidate(video, out var libraryId) && ToSource(video, libraryId) is { } file)
                    Apply(file, store.UpsertSources([file])[0]);
                else RemoveSource(itemId);
            });
        }
        if (processed.Count == 0) return;
        index.RefreshItems(processed);
        catalog.MarkDirty(processed);
    }

    private void Upsert(IReadOnlyList<SourceFile> files)
    {
        var results = store.UpsertSources(files);
        for (var i = 0; i < files.Count; i++)
        {
            var (file, result) = (files[i], results[i]);
            Isolated(file.ItemId, () => Apply(file, result));
        }
    }

    // A changed or never hashed file is probed; a hashed one only needs scheduling.
    private void Apply(SourceFile file, UpsertResult result)
    {
        var now = DateTimeOffset.UtcNow;
        switch (result.Change)
        {
            case SourceChange.Unchanged when result.PreviousSha is { } sha:
                _toSchedule[sha] = 1;
                break;
            case SourceChange.ContentChanged:
                store.CancelProbeJobs(file.ItemId, now);
                store.EnsureProbeJob(new StoredSource(file, null), now);
                // The hash the file had until now may have lost its last file.
                if (result.PreviousSha is { } previous) store.CancelUnreferencedEncodeJob(previous, now);
                break;
            default:
                store.EnsureProbeJob(new StoredSource(file, null), now);
                break;
        }
    }

    // A file that left the library (or is no longer a candidate); its hash may have lost its last file.
    private void RemoveSource(Guid itemId)
    {
        var now = DateTimeOffset.UtcNow;
        var previous = store.RemoveSource(itemId);
        store.CancelProbeJobs(itemId, now);
        if (previous is not null) store.CancelUnreferencedEncodeJob(previous, now);
    }

    private void Isolated(Guid itemId, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理条目 {ItemId} 失败。", itemId);
        }
    }

    private void CollectOrphans()
    {
        var now = DateTimeOffset.UtcNow;
        store.RefreshOrphans(now);
        var deleted = store.DeleteExpiredOrphans(now - TimeSpan.FromHours(Plugin.Instance.Configuration.OrphanRetentionHours), now);
        index.RefreshMedia(deleted);
        _nextOrphanRun = now + Interval;
    }

    // Null when the file has no known length (not in the library metadata and not on disk).
    private static SourceFile? ToSource(Video video, Guid libraryId)
    {
        var length = video.Size ?? (File.Exists(video.Path) ? new FileInfo(video.Path).Length : -1);
        return length < 0 ? null : new SourceFile(video.Id, libraryId, video.Path, length, video.DateModified.ToUniversalTime());
    }
}
