using DistributedHls.Core;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DistributedHls;

// Media sources of a managed item, read from the in-memory index only.
public sealed class SourceListBuilder(PublishedIndex index, AccessPolicy policy, ILibraryManager library)
{
    // q is a published quality, or 0 for "auto" (every published quality).
    public MediaSourceInfo BuildHlsSource(Guid itemId, MediaView m, int q, HttpContext? http)
    {
        var video = (q == 0 ? m.Video.MaxBy(v => v.Summary.Bandwidth) : m.Video.FirstOrDefault(v => v.Summary.Quality == q))?.Summary
            ?? throw new ArgumentException($"没有已发布的 {q}p 视频档位。");
        var id = VersionCatalog.VersionId(library, itemId, q).ToString("N");
        // Stream indexes are the ffprobe indexes of the original file, so they never collide and match the original's tracks.
        var streams = new List<MediaStream>
        {
            new() { Index = m.VideoIndex, Type = MediaStreamType.Video, Codec = "h264", Width = video.Width, Height = video.Height, RealFrameRate = (float)video.FramesPerSecond,
                AverageFrameRate = (float)video.FramesPerSecond, BitRate = video.Bandwidth, BitDepth = 8, PixelFormat = "yuv420p", IsDefault = true }
        };
        foreach (var audio in m.Audio.Select(a => a.Summary))
            streams.Add(new() { Index = audio.SourceIndex, Type = MediaStreamType.Audio, Codec = "aac", Channels = audio.Channels, SampleRate = 48000,
                Language = audio.Language, Title = audio.Title, BitRate = audio.Bandwidth, IsDefault = audio.Default });
        foreach (var subtitle in m.Subtitles.Select(s => s.Summary))
            streams.Add(new() { Index = subtitle.SourceIndex, Type = MediaStreamType.Subtitle, Codec = "webvtt", Language = subtitle.Language, Title = subtitle.Title,
                IsDefault = subtitle.Default, IsExternal = true, SupportsExternalStream = true, DeliveryMethod = SubtitleDeliveryMethod.External });
        var endpoint = $"/Plugins/DistributedHls/Versions/{id}/master.m3u8";
        return new()
        {
            Id = id, Name = q == 0 ? "自动" : $"{q}p", Path = http is null ? endpoint : $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}{endpoint}",
            Protocol = MediaProtocol.Http, Container = "hls", IsRemote = true, SupportsProbing = false, SupportsDirectPlay = false, SupportsDirectStream = false,
            SupportsTranscoding = true, TranscodingSubProtocol = MediaStreamProtocol.hls, TranscodingContainer = "mp4", RunTimeTicks = (long)(m.DurationSeconds * TimeSpan.TicksPerSecond),
            Bitrate = video.Bandwidth + (m.Audio.Count > 0 ? m.Audio.Max(a => a.Summary.Bandwidth) : 0), MediaStreams = streams,
            DefaultAudioStreamIndex = m.Audio.FirstOrDefault(a => a.Summary.Default)?.Summary.SourceIndex ?? m.Audio.FirstOrDefault()?.Summary.SourceIndex,
            DefaultSubtitleStreamIndex = -1, ETag = m.Sha256[..16]
        };
    }

    // Source order: original (when allowed) → fixed qualities (descending) → auto.
    public IReadOnlyList<MediaSourceInfo> Build(BaseItem requested, Video original, IReadOnlyList<MediaSourceInfo> originalSources, User? user, HttpContext? http)
    {
        if (user is not null && !user.HasPermission(PermissionKind.EnableMediaPlayback)) return [];
        var list = new List<MediaSourceInfo>();
        if (policy.OriginalAllowed(user, http))
        {
            var originals = originalSources.Where(s => !IsGeneratedSource(s)).ToList();
            foreach (var source in originals)
                source.Name = originals.Count == 1 || string.IsNullOrEmpty(source.Name) ? "原始质量" : $"原始质量 · {source.Name}";
            list.AddRange(originals);
        }
        if (index.GetMediaForItem(original.Id) is { Playable: true } media && index.GetSource(original.Id) is { } src && policy.LibraryEligible(src.LibraryId))
        {
            list.AddRange(media.Video.Select(v => BuildHlsSource(original.Id, media, v.Summary.Quality, http)));
            list.Add(BuildHlsSource(original.Id, media, 0, http));
        }
        // A version item fetched by its own id keeps its own source first (Android TV plays the first source of the item it fetched).
        if (!VersionCatalog.IsGenerated(requested)) return list;
        var own = requested.Id.ToString("N");
        return [.. list.Where(s => s.Id == own), .. list.Where(s => s.Id != own)];
    }

    private bool IsGeneratedSource(MediaSourceInfo source) =>
        Guid.TryParse(source.Id, out var id) && library.GetItemById(id) is { } item && VersionCatalog.IsGenerated(item);
}
