using System.Collections.Immutable;
using DistributedHls.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

public sealed record IndexedSource(Guid ItemId, Guid LibraryId, string Path, string? Sha256);

// In-memory snapshot for the hot paths (playback, source lists, filters), which never touch SQLite. Rule: whoever commits a change
// to sources, media or renditions calls the matching Refresh method afterwards. Writers rebuild the snapshot under one lock and read
// the store inside it, so the last refresh always sees every earlier commit; readers never lock.
public sealed class PublishedIndex
{
    private sealed record Snapshot(ImmutableDictionary<Guid, IndexedSource> Items, ImmutableDictionary<string, MediaView> Media,
        ImmutableDictionary<string, Guid> PathToItem, ImmutableDictionary<string, ImmutableHashSet<Guid>> ShaToItems);

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly JobStore _store;
    private readonly ILogger<PublishedIndex> _logger;
    private readonly Lock _write = new();
    private Snapshot _snapshot = new(ImmutableDictionary<Guid, IndexedSource>.Empty, ImmutableDictionary<string, MediaView>.Empty,
        ImmutableDictionary.Create<string, Guid>(PathComparer), ImmutableDictionary<string, ImmutableHashSet<Guid>>.Empty);

    // Never throws: the media source decorator, which Jellyfin resolves at startup, depends on this index.
    public PublishedIndex(JobStore store, ILogger<PublishedIndex> logger)
    {
        _store = store;
        _logger = logger;
        Load();
    }

    // Raised after every refresh with the items whose source or media view may have changed.
    public event Action<IReadOnlyCollection<Guid>>? Changed;

    // A store that cannot be read leaves the current snapshot as it is (at startup the empty one).
    public void Load()
    {
        IReadOnlyCollection<Guid> affected;
        lock (_write)
        {
            var items = ImmutableDictionary.CreateBuilder<Guid, IndexedSource>();
            var paths = ImmutableDictionary.CreateBuilder<string, Guid>(PathComparer);
            var shas = new Dictionary<string, HashSet<Guid>>();
            var media = ImmutableDictionary.CreateBuilder<string, MediaView>();
            try
            {
                foreach (var stored in _store.Sources())
                {
                    var source = ToIndexed(stored);
                    items[source.ItemId] = source;
                    if (Key(source.Path) is { } key) paths[key] = source.ItemId;
                    if (source.Sha256 is { } sha)
                    {
                        if (!shas.TryGetValue(sha, out var set)) shas[sha] = set = [];
                        set.Add(source.ItemId);
                    }
                }
                foreach (var group in _store.AllPublished().GroupBy(r => r.Sha256))
                    if (View(_store.GetMedia(group.Key), group) is { } view) media[group.Key] = view;
            }
            catch (Exception ex)
            {
                _logger.LogCritical("HLS 数据库不可用，插件以空数据运行：{Message}", ex.Message);
                return;
            }
            var old = Volatile.Read(ref _snapshot);
            Volatile.Write(ref _snapshot, new Snapshot(items.ToImmutable(), media.ToImmutable(), paths.ToImmutable(),
                shas.ToImmutableDictionary(p => p.Key, p => p.Value.ToImmutableHashSet())));
            // Items that disappeared are affected too: their version items must go.
            affected = [.. old.Items.Keys.Union(items.Keys)];
        }
        Raise(affected);
    }

    public void RefreshItems(IEnumerable<Guid> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return;
        lock (_write)
        {
            var current = Volatile.Read(ref _snapshot);
            var items = current.Items.ToBuilder();
            var paths = current.PathToItem.ToBuilder();
            var shas = current.ShaToItems.ToBuilder();
            foreach (var id in list)
            {
                if (items.TryGetValue(id, out var old))
                {
                    if (Key(old.Path) is { } key && paths.TryGetValue(key, out var owner) && owner == id) paths.Remove(key);
                    if (old.Sha256 is { } sha && shas.TryGetValue(sha, out var set))
                    {
                        set = set.Remove(id);
                        if (set.IsEmpty) shas.Remove(sha);
                        else shas[sha] = set;
                    }
                    items.Remove(id);
                }
                if (_store.GetSource(id) is not { } stored) continue;
                var source = ToIndexed(stored);
                items[id] = source;
                if (Key(source.Path) is { } path) paths[path] = id;
                if (source.Sha256 is { } hash) shas[hash] = (shas.TryGetValue(hash, out var members) ? members : []).Add(id);
            }
            Volatile.Write(ref _snapshot, current with { Items = items.ToImmutable(), PathToItem = paths.ToImmutable(), ShaToItems = shas.ToImmutable() });
        }
        Raise(list);
    }

    // A media that no longer exists, or has nothing published, leaves the snapshot.
    public void RefreshMedia(IEnumerable<string> shas)
    {
        var list = shas.Distinct().ToList();
        if (list.Count == 0) return;
        IReadOnlyCollection<Guid> affected;
        lock (_write)
        {
            var current = Volatile.Read(ref _snapshot);
            var media = current.Media.ToBuilder();
            foreach (var sha in list)
            {
                if (View(_store.GetMedia(sha), _store.Renditions(sha)) is { } view) media[sha] = view;
                else media.Remove(sha);
            }
            Volatile.Write(ref _snapshot, current with { Media = media.ToImmutable() });
            affected = [.. list.SelectMany(sha => current.ShaToItems.TryGetValue(sha, out var set) ? set : ImmutableHashSet<Guid>.Empty).Distinct()];
        }
        Raise(affected);
    }

    public IndexedSource? GetSource(Guid itemId) => Volatile.Read(ref _snapshot).Items.TryGetValue(itemId, out var source) ? source : null;

    public MediaView? GetMediaForItem(Guid itemId)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Items.TryGetValue(itemId, out var source) && source.Sha256 is { } sha && snapshot.Media.TryGetValue(sha, out var view) ? view : null;
    }

    public Guid? FindOriginalByPath(string fullPath) =>
        Key(fullPath) is { } key && Volatile.Read(ref _snapshot).PathToItem.TryGetValue(key, out var itemId) ? itemId : null;

    public IReadOnlyCollection<Guid> ItemsForSha(string sha) =>
        Volatile.Read(ref _snapshot).ShaToItems.TryGetValue(sha, out var items) ? items : ImmutableHashSet<Guid>.Empty;

    private void Raise(IReadOnlyCollection<Guid> affected)
    {
        if (affected.Count > 0) Changed?.Invoke(affected);
    }

    private static IndexedSource ToIndexed(StoredSource s) => new(s.File.ItemId, s.File.LibraryId, s.File.Path, s.Sha256);

    // Paths are compared in their full form; a path that cannot be normalised matches nothing.
    private static string? Key(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // Video by quality descending, audio and subtitles by stream index; the preview is not part of the view.
    private static MediaView? View(MediaRecord? media, IEnumerable<RenditionRow> rows)
    {
        if (media is null) return null;
        var published = rows.Where(r => r.State == RenditionState.Published && r.RelDir is not null && r.Summary is not null)
            .Select(r => new PublishedRendition(r.Key, r.RelDir!, r.Summary!)).ToList();
        if (published.Count == 0) return null;
        return new MediaView(media.Sha256, media.MediaUuid, media.Probe.DurationSeconds, media.Probe.VideoIndex, media.Probe.Audio.Length,
            [.. published.Where(r => r.Key.StartsWith('v')).OrderByDescending(r => r.Summary.Quality)],
            [.. published.Where(r => r.Key.StartsWith('a')).OrderBy(r => r.Summary.SourceIndex)],
            [.. published.Where(r => r.Key.StartsWith('s')).OrderBy(r => r.Summary.SourceIndex)]);
    }
}
