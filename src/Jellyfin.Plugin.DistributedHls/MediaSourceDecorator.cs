using Jellyfin.Api.Extensions;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DistributedHls;

public sealed class MediaSourceDecorator(IMediaSourceManager inner, PublishedIndex index, SourceListBuilder builder, ILibraryManager library, IUserManager users,
    IHttpContextAccessor http) : IMediaSourceManager
{
    // Memory only: a generated version item, or an original the index knows.
    private bool Managed(BaseItem item) => VersionCatalog.IsGenerated(item) || index.GetSource(item.Id) is not null;

    // A caller without a user (DtoService fills MediaStreams that way and takes [0]) gets Jellyfin's own sources. A managed
    // listing that comes out empty (no playback right, original refused, nothing published yet) falls back to the original's sources,
    // so a static list is never emptied by the plugin; only GetPlaybackMediaSources answers with the empty list.
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, User? user = null)
    {
        if (!Managed(item) || user is null) return inner.GetStaticMediaSources(item, enablePathSubstitution, user!);
        if (library.GetItemById(VersionCatalog.OriginalId(item)) is not Video original) return [];
        var originals = inner.GetStaticMediaSources(original, enablePathSubstitution, user);
        var managed = BuildManaged(item, original, originals, user);
        return managed.Count > 0 ? managed : originals;
    }
    // No fallback: an empty list makes PlaybackInfo answer NoCompatibleStream. Without a user (Jellyfin's own stream,
    // subtitle and attachment pipeline, after the route's access control) the real sources are returned; PlaybackInfo always has one.
    public Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item, User user, bool allowMediaProbe, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        if (user is null || !Managed(item)) return inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken);
        IReadOnlyList<MediaSourceInfo> result = library.GetItemById(VersionCatalog.OriginalId(item)) is Video original
            ? BuildManaged(item, original, inner.GetStaticMediaSources(original, enablePathSubstitution, user), user) : [];
        return Task.FromResult(result);
    }
    // A source id missing from the managed list (for example a live stream) is left to Jellyfin.
    public Task<MediaSourceInfo> GetMediaSource(BaseItem item, string mediaSourceId, string liveStreamId, bool enablePathSubstitution, CancellationToken cancellationToken)
    {
        if (!Managed(item)) return inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);
        // GetUserById throws on an empty id, which is what an API key or anonymous request carries.
        var userId = http.HttpContext?.User.GetUserId() ?? Guid.Empty;
        var user = userId == Guid.Empty ? null : users.GetUserById(userId);
        var source = GetStaticMediaSources(item, enablePathSubstitution, user).FirstOrDefault(s => string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase));
        return source is not null ? Task.FromResult(source) : inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);
    }
    public void AddParts(IEnumerable<IMediaSourceProvider> providers) => inner.AddParts(providers);
    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) => inner.GetMediaStreams(itemId);
    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) => inner.GetMediaStreams(query);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => inner.GetMediaAttachments(itemId);
    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => inner.GetMediaAttachments(query);
    public Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken ct) => inner.OpenLiveStream(request, ct);
    public Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, CancellationToken ct) => inner.OpenLiveStreamInternal(request, ct);
    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken ct) => inner.GetLiveStream(id, ct);
    public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(string id, CancellationToken ct) => inner.GetLiveStreamWithDirectStreamProvider(id, ct);
    public ILiveStream GetLiveStreamInfo(string id) => inner.GetLiveStreamInfo(id);
    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => inner.GetLiveStreamInfoByUniqueId(uniqueId);
    public Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(ActiveRecordingInfo info, CancellationToken ct) => inner.GetRecordingStreamMediaSources(info, ct);
    public Task CloseLiveStream(string id) => inner.CloseLiveStream(id);
    public Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken ct) => inner.GetLiveStreamMediaInfo(id, ct);
    public bool SupportsDirectStream(string path, MediaProtocol protocol) => inner.SupportsDirectStream(path, protocol);
    public MediaProtocol GetPathProtocol(string path) => inner.GetPathProtocol(path);
    public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user) => inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
    public Task AddMediaInfoWithProbe(MediaSourceInfo source, bool isAudio, string cacheKey, bool addProbeDelay, bool isLiveStream, CancellationToken ct) => inner.AddMediaInfoWithProbe(source, isAudio, cacheKey, addProbeDelay, isLiveStream, ct);

    // The managed list over the original's own sources, with the user's default audio and subtitle tracks applied.
    private IReadOnlyList<MediaSourceInfo> BuildManaged(BaseItem item, Video original, IReadOnlyList<MediaSourceInfo> originals, User? user)
    {
        var result = builder.Build(item, original, originals, user, http.HttpContext);
        if (user is not null)
            foreach (var source in result) inner.SetDefaultAudioAndSubtitleStreamIndices(original, source, user);
        return result;
    }
}
