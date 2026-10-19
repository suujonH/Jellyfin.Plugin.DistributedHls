using System.Security.Claims;
using DistributedHls.Core;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.MediaInfoDtos;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.DistributedHls;

// Global action filter (order 100): rewrites PlaybackInfo of managed items and guards the real-time stream routes, the action layer
// of the original-file guard. Types are compared before any data is read, so every other request passes untouched.
public sealed class PlaybackFilter(PublishedIndex index, AccessPolicy policy, ILibraryManager library, IUserManager users, SourceListBuilder builder,
    IMediaSourceManager sources) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.Controller is not (MediaInfoController or DynamicHlsController or VideosController or AudioController or UniversalAudioController)
            || !Plugin.Instance.Configuration.Enabled)
        {
            await next();
            return;
        }
        var action = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        // Every DynamicHls action; of the other controllers only the streams (PlaybackInfo for MediaInfoController).
        if ((context.Controller is VideosController && action is not (nameof(VideosController.GetVideoStream) or nameof(VideosController.GetVideoStreamByContainer)))
            || (context.Controller is AudioController && action is not (nameof(AudioController.GetAudioStream) or nameof(AudioController.GetAudioStreamByContainer)))
            || (context.Controller is UniversalAudioController && action is not nameof(UniversalAudioController.GetUniversalAudioStream))
            || (context.Controller is MediaInfoController && action is not (nameof(MediaInfoController.GetPostedPlaybackInfo) or nameof(MediaInfoController.GetPlaybackInfo)))
            || Arg(context, "itemId") is not Guid itemId || itemId == Guid.Empty || library.GetItemById(itemId) is not { } item)
        {
            await next();
            return;
        }
        var playbackInfo = context.Controller is MediaInfoController;
        // A stream plays the item mediaSourceId names, so a route item that is not managed is still guarded when it names a managed
        // one, for example a merged alternate version from another library.
        var selected = playbackInfo ? item : Selected(context, item);
        if (!Managed(item) && !Managed(selected))
        {
            await next();
            return;
        }
        var user = CurrentUser(users, context.HttpContext.User);
        // A version item is only as visible as its original.
        var original = VersionCatalog.IsGenerated(item) ? Find(VersionCatalog.OriginalId(item), user) : item;
        if (original is null)
        {
            context.Result = new NotFoundResult();
            return;
        }
        if (playbackInfo) await PlaybackInfoAsync(context, next, original, user);
        else await StreamAsync(context, next, selected, original, user);
    }

    // GetUserById throws on the empty id that an API key or an anonymous request carries.
    internal static User? CurrentUser(IUserManager users, ClaimsPrincipal principal) =>
        principal.GetUserId() is var id && id != Guid.Empty ? users.GetUserById(id) : null;

    // The quality of one of this original's HLS sources (0 is "auto"), told by comparing the id (ignoring case) with the version ids computed
    // from the original and its published qualities, so a source is recognised before the catalog has created its version item. Null for any other id.
    internal static int? HlsQuality(ILibraryManager library, PublishedIndex index, Guid originalId, string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return null;
        var published = index.GetMediaForItem(originalId)?.Video.Select(v => v.Summary.Quality) ?? [];
        foreach (var quality in published.Prepend(0))
            if (string.Equals(sourceId, VersionCatalog.VersionId(library, originalId, quality).ToString("N"), StringComparison.OrdinalIgnoreCase)) return quality;
        return null;
    }

    // Memory only: a generated version item, or an original the index knows (as in MediaSourceDecorator).
    private bool Managed(BaseItem? item) => item is not null && (VersionCatalog.IsGenerated(item) || index.GetSource(item.Id) is not null);

    // The item a stream plays: the one mediaSourceId names (null when it names none), or the route item when mediaSourceId is missing or unreadable.
    private BaseItem? Selected(ActionExecutingContext context, BaseItem item) =>
        Guid.TryParse(Arg(context, "mediaSourceId") as string, out var sourceId) && sourceId != Guid.Empty && sourceId != item.Id ? library.GetItemById(sourceId) : item;

    // "Auto" is resolved before the action, so the server answers with the source the client gets; the answer is rewritten afterwards.
    private async Task PlaybackInfoAsync(ActionExecutingContext context, ActionExecutionDelegate next, BaseItem original, User? user)
    {
        var http = context.HttpContext;
        var dto = Arg(context, "playbackInfoDto") as PlaybackInfoDto;
        var requested = Arg(context, "mediaSourceId") as string ?? dto?.MediaSourceId;
        // Blank means "not specified", as in MediaInfoHelper.
        if (string.IsNullOrWhiteSpace(requested)) requested = null;
        var autoRequested = string.Equals(requested, VersionCatalog.VersionId(library, original.Id, 0).ToString("N"), StringComparison.OrdinalIgnoreCase);
        // "auto" is the original for a LAN request on a local file the user may use.
        var preferOriginal = policy.AutoPrefersOriginal(original, user, http);
        if (autoRequested && preferOriginal)
        {
            // The request is pointed at the original's own source (not cleared), so the server applies the requested audio and
            // subtitle tracks to it. Otherwise the auto id stays and the server answers with just the auto source.
            var originalSourceId = original.Id.ToString("N");
            if (context.ActionArguments.ContainsKey("mediaSourceId")) context.ActionArguments["mediaSourceId"] = originalSourceId;
            if (dto is not null) dto.MediaSourceId = originalSourceId;
        }
        var executed = await next();
        if (executed.Result is not ObjectResult { Value: PlaybackInfoResponse info }) return;
        var token = HlsDeliveryController.IssueToken(original.Id);
        var audio = Arg(context, "audioStreamIndex") as int? ?? dto?.AudioStreamIndex;
        var media = index.GetMediaForItem(original.Id);
        var offeredOriginal = false;
        var kept = new List<(MediaSourceInfo Source, int? Quality)>();
        foreach (var source in info.MediaSources)
        {
            var quality = HlsQuality(library, index, original.Id, source.Id);
            if (quality is { } q) ApplyHls(source, q, token, info.PlaySessionId, audio, media, http.Request);
            else
            {
                offeredOriginal = true;
                // An original this device can neither play directly nor have transcoded is not offered: the original follows the user's
                // transcoding rights.
                if (!source.SupportsDirectPlay && string.IsNullOrEmpty(source.TranscodingUrl)) continue;
            }
            kept.Add((source, quality));
        }
        var firstOriginal = kept.Where(s => s.Quality is null).Select(s => s.Source).FirstOrDefault();
        var auto = kept.Where(s => s.Quality == 0).Select(s => s.Source).FirstOrDefault();
        var list = kept.Select(s => s.Source).ToList();
        if (autoRequested)
        {
            if (!preferOriginal) info.MediaSources = auto is null ? [] : [auto];
            // The original when this device can play it; otherwise "auto", which the server did not see (it knew the original alone). Nothing
            // is built when no original was offered at all: that user may not play.
            else if (firstOriginal is not null) info.MediaSources = [firstOriginal];
            else info.MediaSources = offeredOriginal && AutoSource(original, user, http, info.PlaySessionId, token, audio, media) is { } built ? [built] : [];
        }
        // Without a requested source the preferred one goes first, the rest keep their order.
        else if (requested is null && (preferOriginal && firstOriginal is not null ? firstOriginal : auto) is { } preferred) info.MediaSources = [preferred, .. list.Where(s => s != preferred)];
        else info.MediaSources = list;
        // Nothing is added back, so an empty answer stays NoCompatibleStream.
        if (info.MediaSources.Count > 0) info.ErrorCode = null;
        else info.ErrorCode ??= PlaybackErrorCode.NoCompatibleStream;
    }

    // The auto source, built as the managed list builds it (media and library guards included), with the user's default tracks as the decorator
    // sets them, then given the fields of every HLS source.
    private MediaSourceInfo? AutoSource(BaseItem original, User? user, HttpContext http, string? playSessionId, string token, int? audio, MediaView? media)
    {
        if (media is not { Playable: true } || index.GetSource(original.Id) is not { } source || !policy.LibraryEligible(source.LibraryId)) return null;
        var auto = builder.BuildHlsSource(original.Id, media, 0, http);
        if (user is not null) sources.SetDefaultAudioAndSubtitleStreamIndices(original, auto, user);
        ApplyHls(auto, 0, token, playSessionId, audio, media, http.Request);
        return auto;
    }

    // The field settings of v1 (PlaybackFilter.cs:42-57), pointed at the token URLs.
    private static void ApplyHls(MediaSourceInfo source, int quality, string token, string? playSessionId, int? audio, MediaView? media, HttpRequest request)
    {
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        // This is a compatibility branch in the clients; it never starts Jellyfin's encoder.
        source.SupportsTranscoding = true;
        source.TranscodingSubProtocol = MediaStreamProtocol.hls;
        source.TranscodingContainer = "mp4";
        var name = HlsDeliveryController.PlaylistName(media, quality, audio ?? source.DefaultAudioStreamIndex);
        source.TranscodingUrl = $"{HlsDeliveryController.Prefix}/{token}/{name}.m3u8?PlaySessionId={playSessionId}&MediaSourceId={source.Id}";
        source.Path = $"{request.Scheme}://{request.Host}{request.PathBase}/{source.TranscodingUrl}";
        foreach (var subtitle in source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle))
        {
            subtitle.DeliveryMethod = SubtitleDeliveryMethod.External;
            subtitle.DeliveryUrl = $"/{HlsDeliveryController.Prefix}/{token}/r/subtitles/{subtitle.Index}/subtitle.vtt";
            subtitle.IsExternalUrl = false;
        }
    }

    // Fails closed: a missing or unreadable mediaSourceId means the item itself, so the original's rules apply.
    private async Task StreamAsync(ActionExecutingContext context, ActionExecutionDelegate next, BaseItem? selected, BaseItem original, User? user)
    {
        // An HLS source of this original is told by its computed id, whether or not its version item exists. Identify is the
        // secondary path for a version item that does exist: the route item itself when mediaSourceId is missing, or a leftover or foreign one.
        var quality = HlsQuality(library, index, original.Id, Arg(context, "mediaSourceId") as string);
        if (quality is null && VersionCatalog.Identify(selected) is { } version)
        {
            // A version of another item.
            if (version.ItemId != original.Id)
            {
                context.Result = new NotFoundResult();
                return;
            }
            quality = version.Quality;
        }
        if (quality is { } q)
        {
            // The audio routes have no audio-only HLS to go to.
            if (context.Controller is AudioController or UniversalAudioController)
            {
                context.Result = new NotFoundResult();
                return;
            }
            var name = HlsDeliveryController.PlaylistName(index.GetMediaForItem(original.Id), q, Arg(context, "audioStreamIndex") as int?);
            context.Result = new RedirectResult($"{context.HttpContext.Request.PathBase}/{HlsDeliveryController.Prefix}/{HlsDeliveryController.IssueToken(original.Id)}/{name}.m3u8");
            return;
        }
        if (context.HttpContext.User.GetIsApiKey())
        {
            await next();
            return;
        }
        if (!policy.OriginalAllowed(user, context.HttpContext))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }
        await next();
    }

    private static object? Arg(ActionExecutingContext context, string name) => context.ActionArguments.TryGetValue(name, out var value) ? value : null;

    private BaseItem? Find(Guid id, User? user) => id == Guid.Empty ? null : library.GetItemById<BaseItem>(id, user);
}
