using DistributedHls.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

// Works through the persistent deletion queue; a failure only postpones its own directory and never blocks dispatching.
public sealed class DeletionService(JobStore store, OutputRootMonitor monitor, ILogger<DeletionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            do
            {
                try
                {
                    // Nothing to do without the database. While the output root cannot be trusted no row is touched
                    // and no attempt is used up.
                    if (store.InitError is not null || monitor.Check() is not null) continue;
                    var rows = store.DueDeletions(DateTimeOffset.UtcNow, 16);
                    await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = stoppingToken }, (row, _) =>
                    {
                        Delete(row);
                        return ValueTask.CompletedTask;
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "处理 HLS 删除队列失败。");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        // An exception leaving a BackgroundService stops the whole server.
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical(ex, "HLS 删除服务意外停止。");
        }
    }

    private void Delete(DeletionRow row)
    {
        // Checked again before anything is deleted: only directories of the generated layout are ever touched.
        if (!RenditionPaths.IsValidRelativeDirectory(row.RelDir))
        {
            logger.LogError("删除队列中的目录不合法，已移出队列且未删除任何文件：{RelDir}", row.RelDir);
            store.CompleteDeletion(row.RelDir);
            return;
        }
        var root = Plugin.Instance.Configuration.OutputPath;
        try
        {
            var full = RenditionPaths.Full(root, row.RelDir);
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
            // The directory may only seem gone because the root became unavailable since the tick began; the row then stays due.
            else if (monitor.Check() is not null) return;
            // Then {uuid}/{kind}/{index} (previews have no such level), {uuid}/{kind} and {uuid}, each only while empty.
            var parts = row.RelDir.Split('/');
            for (var depth = parts.Length - 1; depth >= 1; depth--) TryDeleteEmpty(RenditionPaths.Full(root, string.Join('/', parts[..depth])));
            store.CompleteDeletion(row.RelDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var next = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, row.Attempts)));
            store.PostponeDeletion(row.RelDir, ex.Message, next);
            logger.LogWarning(ex, "删除 {RelDir} 失败，将于 {Next} 后重试。", row.RelDir, next);
        }
    }

    private static void TryDeleteEmpty(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A parent that is not empty (or just got a new entry) stays.
        }
    }
}
