using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.DistributedHls;

// Every decision reads the live plugin configuration.
public sealed class AccessPolicy(INetworkManager network)
{
    public bool OriginalAllowed(User? user, HttpContext? http)
    {
        var c = Plugin.Instance.Configuration;
        return !c.Enabled || (user is not null && c.OriginalUsers.Contains(user.Id)) || (c.AllowLocalOriginal && IsLan(http));
    }

    public bool IsLan(HttpContext? http) => http is not null && network.IsInLocalNetwork(http.GetNormalizedRemoteIP());

    public bool LibraryEligible(Guid libraryId)
    {
        var c = Plugin.Instance.Configuration;
        return c.Enabled && (c.Libraries.Length == 0 || c.Libraries.Contains(libraryId));
    }

    // "auto" means the original for a LAN request on a local file the user may play directly.
    public bool AutoPrefersOriginal(BaseItem original, User? user, HttpContext? http) =>
        OriginalAllowed(user, http) && IsLan(http) && original.IsFileProtocol && !original.IsShortcut;
}
