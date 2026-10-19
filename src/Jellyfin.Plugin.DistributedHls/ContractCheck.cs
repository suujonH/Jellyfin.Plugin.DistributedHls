using Jellyfin.Api.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

// Checks once at startup that the Jellyfin actions and parameters the playback filter reads still exist. It only reports:
// the guards fail closed on their own (a missing mediaSourceId is treated as the original).
public sealed class ContractCheck(IActionDescriptorCollectionProvider actions, ILogger<ContractCheck> logger) : IHostedService
{
    private static readonly (Type Controller, string[] Actions, string[] Parameters)[] Contract =
    [
        (typeof(MediaInfoController), [nameof(MediaInfoController.GetPostedPlaybackInfo)], ["itemId", "mediaSourceId", "audioStreamIndex", "playbackInfoDto"]),
        (typeof(VideosController), [nameof(VideosController.GetVideoStream)], ["itemId", "static", "mediaSourceId"]),
        (typeof(DynamicHlsController), [nameof(DynamicHlsController.GetMasterHlsVideoPlaylist), nameof(DynamicHlsController.GetVariantHlsVideoPlaylist),
            nameof(DynamicHlsController.GetHlsVideoSegment), nameof(DynamicHlsController.GetLiveHlsStream)], ["itemId", "mediaSourceId"]),
        // The audio streams are guarded like the video ones.
        (typeof(AudioController), [nameof(AudioController.GetAudioStream), nameof(AudioController.GetAudioStreamByContainer)], ["itemId", "mediaSourceId"]),
        (typeof(UniversalAudioController), [nameof(UniversalAudioController.GetUniversalAudioStream)], ["itemId", "mediaSourceId"])
    ];

    private volatile IReadOnlyList<string> _problems = [];

    // Empty when every action and parameter was found.
    public IReadOnlyList<string> Problems => _problems;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var problems = new List<string>();
        try
        {
            var descriptors = actions.ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToList();
            foreach (var (controller, names, parameters) in Contract)
                foreach (var name in names)
                {
                    // One descriptor per route (GET and HEAD of the same method), each must have every parameter.
                    var found = descriptors.Where(d => d.ControllerTypeInfo.AsType() == controller && d.ActionName == name).ToList();
                    if (found.Count == 0) problems.Add($"缺少动作 {controller.Name}.{name}");
                    else problems.AddRange(parameters.Where(p => !found.All(d => d.Parameters.Any(x => x.Name == p))).Select(p => $"{controller.Name}.{name} 缺少参数 {p}"));
                }
            foreach (var problem in problems)
                logger.LogCritical("HLS 播放所依赖的 Jellyfin 接口已变化：{Problem}。原始资源防护仍按“失败即拒绝”生效，但 HLS 播放可能不可用。", problem);
        }
        // A failing check never stops the server.
        catch (Exception ex)
        {
            logger.LogCritical(ex, "HLS 播放接口自检失败。");
            problems.Add("自检失败：" + ex.Message);
        }
        _problems = problems;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
