using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DistributedHls;

public sealed record PlaybackOptionsResult(string? PreferredMediaSourceId, IReadOnlyList<string> ManagedMediaSourceIds);

[ApiController]
[Authorize]
[Route("Plugins/DistributedHls")]
public sealed class PlaybackController(PublishedIndex index, SourceListBuilder builder, ILibraryManager library, IUserManager users, IMediaSourceManager sources)
    : ControllerBase
{
    // Compatibility entry: the static Path of an HLS source points here; the session is exchanged for a token URL.
    [HttpGet("Versions/{versionId:guid}/master.m3u8")]
    public IActionResult VersionPlaylist(Guid versionId, int? audioStreamIndex)
    {
        var user = PlaybackFilter.CurrentUser(users, User);
        if (user is null || !user.HasPermission(PermissionKind.EnableMediaPlayback)) return new StatusCodeResult(StatusCodes.Status403Forbidden);
        if (VersionCatalog.Identify(Find(versionId, user)) is not { } version || Find(version.ItemId, user) is null
            || index.GetMediaForItem(version.ItemId) is not { Playable: true } media) return NotFound();
        var name = HlsDeliveryController.PlaylistName(media, version.Quality, audioStreamIndex);
        return Redirect($"{Request.PathBase}/{HlsDeliveryController.Prefix}/{HlsDeliveryController.IssueToken(version.ItemId)}/{name}.m3u8");
    }

    // For the legacy web: which sources are HLS, and which source to preselect.
    [HttpGet("Items/{itemId:guid}/PlaybackOptions")]
    public IActionResult PlaybackOptions(Guid itemId)
    {
        var user = PlaybackFilter.CurrentUser(users, User);
        if (Find(itemId, user) is not { } item) return NotFound();
        if (!(VersionCatalog.IsGenerated(item) || index.GetSource(item.Id) is not null) || Find(VersionCatalog.OriginalId(item), null) is not Video original)
            return Ok(new PlaybackOptionsResult(null, []));
        // Without a user the decorator returns Jellyfin's own sources of the original.
        var list = builder.Build(item, original, sources.GetStaticMediaSources(original, true, null), user, HttpContext)
            .Select(s => (Source: s, Quality: PlaybackFilter.HlsQuality(library, index, original.Id, s.Id))).ToList();
        // "auto" is preselected whenever it is offered, also where the original would be preferred.
        // PlaybackInfo resolves auto to the original on the LAN when this device can play the original, and falls back to HLS when it
        // cannot; preselecting the original would lose that fallback. Null without an auto source.
        var auto = list.Where(s => s.Quality == 0).Select(s => s.Source).FirstOrDefault();
        return Ok(new PlaybackOptionsResult(auto?.Id, [.. list.Where(s => s.Quality is not null).Select(s => s.Source.Id)]));
    }

    private BaseItem? Find(Guid id, User? user) => id == Guid.Empty ? null : library.GetItemById<BaseItem>(id, user);
}
