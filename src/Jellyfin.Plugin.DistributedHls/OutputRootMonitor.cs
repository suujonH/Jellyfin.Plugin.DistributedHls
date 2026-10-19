using DistributedHls.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

// Whoever decides from a file or directory missing in the output root (verification, re-verification, deletion) asks this
// first; nothing irreversible happens while the root cannot be trusted. Only a change of the state is logged.
public sealed class OutputRootMonitor(JobStore store, ILogger<OutputRootMonitor> logger)
{
    private string? _problem;

    // The result of the last check: null while the root is usable.
    public string? Problem => Volatile.Read(ref _problem);

    // Reads the live configuration and the store; null when the root is usable, otherwise the reason.
    public string? Check()
    {
        var problem = OutputRoot.Check(Plugin.Instance.Configuration.OutputPath, store.PublishedSample(5));
        if (Interlocked.Exchange(ref _problem, problem) == problem) return problem;
        if (problem is not null) logger.LogWarning("输出目录不可用，暂停校验和删除：{Problem}", problem);
        else logger.LogInformation("输出目录已恢复可用。");
        return problem;
    }
}
