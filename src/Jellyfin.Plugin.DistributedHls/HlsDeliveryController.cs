using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using DistributedHls.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DistributedHls;

// Anonymous HLS delivery. The grant sits in the path, so every relative URI of the playlists carries it and no client needs
// an authentication header. Files go out as plain PhysicalFile results: how they reach the client is left to the result
// filters of whatever storage the output directory lives on.
[ApiController]
[AllowAnonymous]
[Route(Prefix + "/{token}")]
public sealed partial class HlsDeliveryController(PublishedIndex published, AccessPolicy policy) : ControllerBase
{
    internal const string Prefix = "Plugins/DistributedHls/Hls";
    private const string PlaylistMime = "application/vnd.apple.mpegurl";

    // A grant for the HLS of one original, valid for PlaybackTokenHours.
    internal static string IssueToken(Guid itemId)
    {
        var config = Plugin.Instance.Configuration;
        return PlaybackGrant.Create(itemId, DateTimeOffset.UtcNow.AddHours(config.PlaybackTokenHours), Convert.FromBase64String(config.PlaybackSigningKey));
    }

    // "master" (every quality) or "{q}p"; "-a{index}" only for a published audio track.
    internal static string PlaylistName(MediaView? media, int quality, int? audio) =>
        (quality == 0 ? "master" : $"{quality}p") + (audio is { } a && media is not null && media.Audio.Any(t => t.Summary.SourceIndex == a) ? $"-a{a}" : "");

    [HttpGet("{playlist}.m3u8")]
    [HttpHead("{playlist}.m3u8", Name = "HeadDistributedHlsPlaylist")]
    public IActionResult MasterPlaylist(string token, string playlist)
    {
        if (!TryAuthorize(token, out var media, out var refused)) return refused;
        var match = PlaylistPattern().Match(playlist);
        if (!match.Success) return NotFound();
        var quality = match.Groups["q"].Success ? int.Parse(match.Groups["q"].ValueSpan, CultureInfo.InvariantCulture) : 0;
        int? audio = match.Groups["a"].Success ? int.Parse(match.Groups["a"].ValueSpan, CultureInfo.InvariantCulture) : null;
        // Only what is published now: a quality or an audio track that was withdrawn is not offered.
        if ((quality != 0 && !media.Video.Any(v => v.Summary.Quality == quality)) || (audio is { } a && !media.Audio.Any(t => t.Summary.SourceIndex == a)))
            return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Content(MasterPlaylistWriter.Write(media, quality, audio), PlaylistMime);
    }

    // The URIs of the master playlist (r/video/{q}/…, r/audio/{index}/…, r/subtitles/{index}/…) and, relative to those, the files.
    [HttpGet("r/{kind}/{index:int}/{file}")]
    [HttpHead("r/{kind}/{index:int}/{file}", Name = "HeadDistributedHlsRendition")]
    public IActionResult RenditionFile(string token, string kind, int index, string file)
    {
        if (!TryAuthorize(token, out var media, out var refused)) return refused;
        var rendition = kind switch
        {
            "video" => media.Video.FirstOrDefault(r => r.Summary.Quality == index),
            "audio" => media.Audio.FirstOrDefault(r => r.Summary.SourceIndex == index),
            "subtitles" => media.Subtitles.FirstOrDefault(r => r.Summary.SourceIndex == index),
            _ => null
        };
        if (rendition is null || !FilePattern().IsMatch(file) || file.Contains("..", StringComparison.Ordinal)) return NotFound();
        var mime = Path.GetExtension(file) switch
        {
            ".m3u8" => PlaylistMime,
            ".mp4" => "video/mp4",
            ".m4s" => "video/iso.segment",
            ".vtt" => "text/vtt",
            _ => null
        };
        // The type is checked first: it needs no file system access.
        if (mime is null) return NotFound();
        var full = Path.Combine(RenditionPaths.Full(Plugin.Instance.Configuration.OutputPath, rendition.RelDir), file);
        if (!System.IO.File.Exists(full)) return NotFound();
        Response.Headers.CacheControl = mime == PlaylistMime ? "no-store" : "private, max-age=86400";
        return PhysicalFile(full, mime, enableRangeProcessing: true);
    }

    // Shared by both actions: a valid grant (403 otherwise), then a playable media of an eligible library (404 otherwise).
    private bool TryAuthorize(string token, [NotNullWhen(true)] out MediaView? media, [NotNullWhen(false)] out IActionResult? refused)
    {
        var config = Plugin.Instance.Configuration;
        media = null;
        if (!PlaybackGrant.TryVerify(token, Convert.FromBase64String(config.PlaybackSigningKey), DateTimeOffset.UtcNow, out var itemId))
        {
            refused = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return false;
        }
        var view = published.GetMediaForItem(itemId);
        var source = published.GetSource(itemId);
        if (!config.Enabled || view is not { Playable: true } || source is null || !policy.LibraryEligible(source.LibraryId))
        {
            refused = NotFound();
            return false;
        }
        media = view;
        refused = null;
        return true;
    }

    // ASCII digits and "\z": "\d" also accepts other scripts' digits and "$" a trailing line feed.
    [GeneratedRegex(@"^(?:master|(?<q>[0-9]{3,4})p)(?:-a(?<a>[0-9]{1,3}))?\z")]
    private static partial Regex PlaylistPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")]
    private static partial Regex FilePattern();
}
