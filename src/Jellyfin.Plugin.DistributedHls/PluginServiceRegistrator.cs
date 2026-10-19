using DistributedHls.Core;
using Jellyfin.Api.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DistributedHls;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton(sp => new JobStore(Path.Combine(sp.GetRequiredService<IApplicationPaths>().DataPath, "distributed-hls", "jobs.db")));
        services.AddSingleton<PublishedIndex>();
        services.AddSingleton<OutputRootMonitor>();
        services.AddSingleton<AccessPolicy>();
        services.AddSingleton<SourceListBuilder>();
        services.AddSingleton<VersionCatalog>();
        // Each hosted service is the same singleton the controllers use.
        services.AddSingleton<Coordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<Coordinator>());
        services.AddSingleton<VerificationService>();
        services.AddHostedService(sp => sp.GetRequiredService<VerificationService>());
        services.AddSingleton<DeletionService>();
        services.AddHostedService(sp => sp.GetRequiredService<DeletionService>());
        services.AddSingleton<ContractCheck>();
        services.AddHostedService(sp => sp.GetRequiredService<ContractCheck>());
        var original = services.Last(d => d.ServiceType == typeof(IMediaSourceManager));
        services.Remove(original);
        services.AddSingleton<IMediaSourceManager>(sp =>
        {
            var inner = (IMediaSourceManager)(original.ImplementationInstance ?? original.ImplementationFactory?.Invoke(sp)
                ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!));
            return ActivatorUtilities.CreateInstance<MediaSourceDecorator>(sp, inner);
        });
        services.AddScoped<PlaybackFilter>();
        services.AddScoped<OriginalFileGuardFilter>();
        services.AddAuthorization(options => options.AddPolicy("DistributedHls.Worker", policy => policy.RequireAuthenticatedUser().RequireAssertion(c => c.User.GetIsApiKey())));
        services.Configure<MvcOptions>(options =>
        {
            options.Filters.AddService<PlaybackFilter>(100);
            // A low order: result filters that rewrite file delivery (higher orders) never see a refused original.
            options.Filters.AddService<OriginalFileGuardFilter>(100);
        });
    }
}
