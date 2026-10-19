using Jellyfin.Api.Controllers;
using Jellyfin.Api.Extensions;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.DistributedHls;

// The result layer of the original-file guard (global, order 100, so result filters with a higher order only see what it let through): any
// result that hands out the file of a managed original is refused unless the original is allowed. Covers /Items/{id}/File,
// /Items/{id}/Download, static streams and any route that returns an original later on, whatever its parameters.
public sealed class OriginalFileGuardFilter(PublishedIndex index, AccessPolicy policy, IUserManager users) : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (!Plugin.Instance.Configuration.Enabled || http.User.GetIsApiKey())
        {
            await next();
            return;
        }
        var path = context.Result switch
        {
            PhysicalFileResult file => file.FileName,
            FileStreamResult { FileStream: FileStream stream } => stream.Name,
            _ => null
        };
        if (path is null || FindOriginal(context, path) is not { } itemId || index.GetSource(itemId) is not { } source || !policy.LibraryEligible(source.LibraryId)
            || policy.OriginalAllowed(PlaybackFilter.CurrentUser(users, http.User), http))
        {
            await next();
            return;
        }
        if (context.Result is FileStreamResult { FileStream: FileStream open }) await open.DisposeAsync();
        context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
        await next();
    }

    // By path; and for LibraryController (/Items/{id}/File and /Download), whose only files are the route item's own file, by that item:
    // there Jellyfin returns the final target of a symlinked file, a path the index does not know (LibraryController.cs:122-133, 709-720).
    private Guid? FindOriginal(ResultExecutingContext context, string path)
    {
        if (index.FindOriginalByPath(path) is { } byPath) return byPath;
        if (context.Controller is not LibraryController || !context.RouteData.Values.TryGetValue("itemId", out var value)
            || !Guid.TryParse(value as string, out var itemId) || index.GetSource(itemId) is null) return null;
        return itemId;
    }
}
