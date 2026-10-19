using System.Collections.Concurrent;
using System.Globalization;
using DistributedHls.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

// Keeps one real Video item per published quality, plus "auto", next to each original. A version item only carries
// PrimaryVersionId: no OwnerId and no link from the original, so progress stays on the original; version_items finds each item by id.
public sealed class VersionCatalog
{
    public const string Marker = "DistributedHls";
    // Every quality v1 may have created, plus "auto".
    private static readonly int[] LegacyQualities = [0, 2160, 1440, 1080, 720, 480, 360, 240, 144];

    private readonly JobStore _store;
    private readonly PublishedIndex _index;
    private readonly AccessPolicy _policy;
    private readonly SourceListBuilder _builder;
    private readonly ILibraryManager _library;
    private readonly IMediaStreamRepository _streams;
    private readonly ILogger<VersionCatalog> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _dirty = new();
    // Set by RemoveAllAsync: from then on a sync only removes, so nothing is created again before the server restarts.
    private volatile bool _removing;

    public VersionCatalog(JobStore store, PublishedIndex index, AccessPolicy policy, SourceListBuilder builder, ILibraryManager library,
        IMediaStreamRepository streams, ILogger<VersionCatalog> logger)
    {
        _store = store;
        _index = index;
        _policy = policy;
        _builder = builder;
        _library = library;
        _streams = streams;
        _logger = logger;
        index.Changed += MarkDirty;
    }

    public static bool IsGenerated(BaseItem item) => item.ProviderIds.ContainsKey(Marker);

    // The marker value is "{original N}:{quality}"; v1 values carry a third segment, which is ignored.
    public static (Guid ItemId, int Quality)? Identify(BaseItem? item)
    {
        if (item is null || !item.ProviderIds.TryGetValue(Marker, out var value)) return null;
        var parts = value.Split(':');
        return parts.Length >= 2 && Guid.TryParse(parts[0], out var itemId) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var quality)
            ? (itemId, quality) : null;
    }

    public static Guid OriginalId(BaseItem item) => Identify(item)?.ItemId ?? item.Id;

    // The v1 formula, so a version item left by v1 is updated in place.
    public static Guid VersionId(ILibraryManager library, Guid itemId, int q) => library.GetNewItemId(VersionPath(itemId, q), typeof(Video));

    private static string VersionPath(Guid itemId, int q) => $"https://distributed-hls.invalid/{itemId:N}/{q}/master.m3u8";

    public void MarkDirty(IEnumerable<Guid> ids)
    {
        foreach (var id in ids) _dirty[id] = 0;
    }

    public void MarkAllDirty()
    {
        MarkDirty(_store.Sources().Select(s => s.File.ItemId));
        MarkDirty(_store.ItemsWithVersions());
    }

    public async Task ProcessDirtyAsync(int max, CancellationToken ct)
    {
        foreach (var itemId in _dirty.Take(max).Select(p => p.Key).ToList())
        {
            if (!_dirty.TryRemove(itemId, out _)) continue;
            try
            {
                await SyncItemAsync(itemId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "同步条目 {ItemId} 的 HLS 版本失败。", itemId);
            }
        }
    }

    // Before the first sync: v1 version items are found by the old id formula, deleted, and the original's v1 links removed.
    public async Task CleanupLegacyAsync(CancellationToken ct)
    {
        foreach (var itemId in _store.LegacyItems())
        {
            try
            {
                foreach (var q in LegacyQualities)
                    if (_library.GetItemById(VersionId(_library, itemId, q)) is { } version && IsGenerated(version)) Delete(version);
                if (_library.GetItemById(itemId) is Video original) await RemoveGeneratedLinksAsync(original, ct);
                _store.RemoveLegacyItem(itemId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "清理条目 {ItemId} 的 v1 版本失败，下次启动时重试。", itemId);
            }
        }
    }

    // Called on uninstall.
    public async Task RemoveAllAsync(CancellationToken ct)
    {
        _removing = true;
        foreach (var itemId in _store.ItemsWithVersions())
        {
            try
            {
                foreach (var (q, entry) in _store.VersionItems(itemId))
                {
                    if (_library.GetItemById(entry.Id) is { } version && IsGenerated(version)) Delete(version);
                    _store.RemoveVersionItem(itemId, q);
                }
                if (_library.GetItemById(itemId) is Video original) await RemoveGeneratedLinksAsync(original, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "删除条目 {ItemId} 的 HLS 版本失败。", itemId);
            }
        }
    }

    // A deleted original arrives here too (original null, nothing desired), so its version items are removed by id.
    private async Task SyncItemAsync(Guid itemId, CancellationToken ct)
    {
        var original = _library.GetItemById(itemId) as Video;
        var existing = _store.VersionItems(itemId);
        var desired = new HashSet<int>();
        if (!_removing && original is not null && _index.GetSource(itemId) is { } source && _policy.LibraryEligible(source.LibraryId)
            && _index.GetMediaForItem(itemId) is { Playable: true } media)
        {
            var signature = $"{EncodingProfile.Version}|{media.Sha256}|{Keys(media.Video)}|{Keys(media.Audio)}|{Keys(media.Subtitles)}|{original.DateModified.Ticks}|{original.ImageInfos.Length}";
            foreach (var q in media.Video.Select(v => v.Summary.Quality).Prepend(0))
            {
                desired.Add(q);
                var id = VersionId(_library, itemId, q);
                if (_library.GetItemById(id) is not Video version)
                {
                    version = new Video { Id = id, Path = VersionPath(itemId, q) };
                    ApplyFields(version, original, media, q);
                    _library.CreateItem(version, original.GetParent());
                }
                else
                {
                    var differs = ApplyFields(version, original, media, q);
                    if (!differs && existing.TryGetValue(q, out var stored) && stored.Signature == signature) continue;
                    await _library.UpdateItemAsync(version, version.GetParent(), ItemUpdateType.MetadataEdit, ct);
                }
                _streams.SaveMediaStreams(id, _builder.BuildHlsSource(itemId, media, q, null).MediaStreams, ct);
                _store.SetVersionItem(itemId, q, id, signature);
            }
        }
        foreach (var (q, entry) in existing)
        {
            if (desired.Contains(q)) continue;
            if (_library.GetItemById(entry.Id) is { } version && IsGenerated(version)) Delete(version);
            _store.RemoveVersionItem(itemId, q);
        }
        if (original is not null) await RemoveGeneratedLinksAsync(original, ct);
    }

    private void Delete(BaseItem version) => _library.DeleteItem(version, new DeleteOptions { DeleteFileLocation = false });

    // v1 linked its versions to the original; v2 never does, so any generated link left on an original is removed.
    private async Task RemoveGeneratedLinksAsync(Video original, CancellationToken ct)
    {
        var kept = original.LinkedAlternateVersions.Where(l => !(l.ItemId is { } id && _library.GetItemById(id) is { } item && IsGenerated(item))).ToArray();
        if (kept.Length == original.LinkedAlternateVersions.Length) return;
        original.LinkedAlternateVersions = kept;
        await _library.UpdateItemAsync(original, original.GetParent(), ItemUpdateType.MetadataEdit, ct);
    }

    // Sets every field a version item takes from its original and media; returns whether any value differed before.
    private static bool ApplyFields(Video v, Video original, MediaView media, int q)
    {
        var parent = original.GetParent() as Folder;
        // "auto" takes the size of the highest quality.
        var size = (q == 0 ? media.Video[0] : media.Video.First(r => r.Summary.Quality == q)).Summary;
        var ticks = (long)(media.DurationSeconds * TimeSpan.TicksPerSecond);
        var marker = $"{original.Id:N}:{q}";
        var differs = v.Name != original.Name || v.ForcedSortName != original.SortName || v.ParentId != (parent?.Id ?? Guid.Empty)
            || v.PrimaryVersionId != original.Id || v.OwnerId != Guid.Empty || v.RunTimeTicks != ticks || v.Container != "hls" || !v.IsLocked
            || v.OfficialRating != original.OfficialRating || v.CustomRating != original.CustomRating || !v.Tags.SequenceEqual(original.Tags)
            || !Images(v.ImageInfos).SequenceEqual(Images(original.ImageInfos)) || v.Overview != original.Overview
            || v.ProductionYear != original.ProductionYear || v.PremiereDate != original.PremiereDate || v.CommunityRating != original.CommunityRating
            || v.Width != size.Width || v.Height != size.Height
            || v.ProviderIds.Count != 1 || !v.ProviderIds.TryGetValue(Marker, out var current) || current != marker;
        v.Name = original.Name;
        v.ForcedSortName = original.SortName;
        v.SetParent(parent);
        v.SetPrimaryVersionId(original.Id);
        v.OwnerId = Guid.Empty;
        v.RunTimeTicks = ticks;
        v.Container = "hls";
        v.IsLocked = true;
        v.OfficialRating = original.OfficialRating;
        v.CustomRating = original.CustomRating;
        v.Tags = [.. original.Tags];
        v.ImageInfos = [.. original.ImageInfos.Select(i => new ItemImageInfo { Path = i.Path, Type = i.Type, DateModified = i.DateModified, Width = i.Width, Height = i.Height, BlurHash = i.BlurHash })];
        v.Overview = original.Overview;
        v.ProductionYear = original.ProductionYear;
        v.PremiereDate = original.PremiereDate;
        v.CommunityRating = original.CommunityRating;
        v.Width = size.Width;
        v.Height = size.Height;
        // Only the marker: provider ids such as Imdb or Tmdb are never copied.
        v.ProviderIds = new(StringComparer.OrdinalIgnoreCase) { [Marker] = marker };
        return differs;
    }

    // The stored order of images is not guaranteed, so they are compared as a sorted list.
    private static IEnumerable<(string, ImageType, DateTime, int, int, string?)> Images(ItemImageInfo[] images) =>
        images.Select(i => (i.Path, i.Type, i.DateModified, i.Width, i.Height, i.BlurHash)).OrderBy(i => i.Item2).ThenBy(i => i.Item1, StringComparer.Ordinal);

    private static string Keys(IEnumerable<PublishedRendition> renditions) => string.Join(',', renditions.Select(r => r.Key));
}
