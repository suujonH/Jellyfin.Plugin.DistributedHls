using System.Threading.Channels;
using DistributedHls.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

// Aborted: why the run stopped before it was done (the database or the output root was unavailable), otherwise null.
public sealed record ReverifyReport(DateTimeOffset At, int Checked, int Failed, string? Aborted = null);

// Publishes a reported manifest once every file is in place, and re-checks published renditions on request.
public sealed class VerificationService(JobStore store, PublishedIndex index, Coordinator coordinator, OutputRootMonitor monitor,
    ILogger<VerificationService> logger) : BackgroundService
{
    private const int BatchSize = 4;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private int _reverifying;
    private volatile ReverifyReport? _lastReverify;

    public ReverifyReport? LastReverify => _lastReverify;

    public void Signal() => _signal.Writer.TryWrite(true);

    // False while a re-verification is already running.
    public bool StartReverify(Guid? itemId)
    {
        if (Interlocked.CompareExchange(ref _reverifying, 1, 0) != 0) return false;
        _ = Task.Run(() =>
        {
            try
            {
                Reverify(itemId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "重新校验已发布成果失败。");
            }
            finally
            {
                Volatile.Write(ref _reverifying, 0);
            }
        });
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A signal queued before the start must not make the host wait for a verification.
        await Task.Yield();
        try
        {
            while (true)
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    wait.CancelAfter(TimeSpan.FromSeconds(10));
                    try
                    {
                        await _signal.Reader.ReadAsync(wait.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Ten seconds without a signal.
                    }
                }
                VerifyBatch();
            }
        }
        // An exception leaving a BackgroundService stops the whole server.
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogCritical(ex, "HLS 校验服务意外停止。");
        }
    }

    // A job that throws stays Verifying and is tried again next round.
    private void VerifyBatch()
    {
        // Nothing to verify without the database.
        if (store.InitError is not null) return;
        IReadOnlyList<VerifyingJob> jobs;
        try
        {
            // While the output root cannot be trusted every job stays Verifying and no attempt is used up.
            if (monitor.Check() is not null) return;
            jobs = store.VerifyingJobs(BatchSize);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "读取待校验任务失败。");
            return;
        }
        foreach (var job in jobs)
        {
            try
            {
                Verify(job);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "校验任务 {JobId} 出错，下一轮重试。", job.Id);
            }
        }
    }

    private void Verify(VerifyingJob job)
    {
        var config = Plugin.Instance.Configuration;
        // Lazy: stops at the first failing rendition.
        var error = job.Outputs.Select(o => OutputVerifier.Verify(RenditionPaths.Full(config.OutputPath, o.RelDir), o.Files, config.VerifyModifiedTime))
            .FirstOrDefault(e => e is not null);
        var now = DateTimeOffset.UtcNow;
        if (error is null)
        {
            // A replaced directory stays as long as a playback grant issued for it may be used.
            if (store.Publish(job.Id, EncodingProfile.Version, now, TimeSpan.FromHours(config.PlaybackTokenHours)) is not { } result) return;
            index.RefreshMedia([result.Sha256]);
            coordinator.EnqueueSchedule(result.Sha256);
            return;
        }
        // The files may only be missing because the root became unavailable since the batch began; the job then stays Verifying.
        if (monitor.Check() is not null) return;
        store.FailVerification(job.Id, "校验失败：" + error, config.MaxAttempts, now);
        coordinator.EnqueueSchedule(job.Sha256);
        logger.LogWarning("任务 {JobId}（{Sha256}）的成果校验失败：{Error}", job.Id, job.Sha256, error);
    }

    // One item's media, or every published media, one media at a time (the manifests of a whole library may not fit in memory).
    // A failing rendition is retired (its directory queued for deletion) and encoded again, but never while the output root cannot be
    // trusted: the run is aborted instead, and the report says why.
    private void Reverify(Guid? itemId)
    {
        if (store.InitError is { } storeError)
        {
            _lastReverify = new(DateTimeOffset.UtcNow, 0, 0, storeError);
            return;
        }
        if (monitor.Check() is { } unavailable)
        {
            _lastReverify = new(DateTimeOffset.UtcNow, 0, 0, unavailable);
            return;
        }
        var config = Plugin.Instance.Configuration;
        IReadOnlyList<string> shas;
        if (itemId is { } id)
        {
            if (index.GetSource(id)?.Sha256 is not { } sha)
            {
                _lastReverify = new(DateTimeOffset.UtcNow, 0, 0);
                return;
            }
            shas = [sha];
        }
        else shas = store.PublishedShas();
        int checkedCount = 0, failed = 0;
        foreach (var sha in shas)
        {
            var retired = false;
            try
            {
                var rootChecked = false;
                foreach (var manifest in store.PublishedManifests(sha))
                {
                    checkedCount++;
                    if (OutputVerifier.Verify(RenditionPaths.Full(config.OutputPath, manifest.RelDir), manifest.Files, config.VerifyModifiedTime) is not { } error) continue;
                    // The first failure of a media asks whether the root is still there; if it is not, nothing more is retired.
                    if (!rootChecked && monitor.Check() is { } problem)
                    {
                        _lastReverify = new(DateTimeOffset.UtcNow, checkedCount, failed, problem);
                        return;
                    }
                    rootChecked = true;
                    failed++;
                    logger.LogWarning("已发布档位 {Sha256}/{Key} 重新校验失败：{Error}", manifest.Sha256, manifest.Key, error);
                    retired |= store.RetireRendition(manifest.Sha256, manifest.Key, manifest.RelDir, "重新校验失败", DateTimeOffset.UtcNow);
                }
            }
            // One media that cannot be read or retired does not end the run.
            catch (Exception ex)
            {
                logger.LogError(ex, "重新校验 {Sha256} 的已发布成果出错，继续下一个媒体。", sha);
            }
            finally
            {
                // Per media, so playback stops using a retired directory before the deletion queue reaches it.
                if (retired)
                {
                    index.RefreshMedia([sha]);
                    coordinator.EnqueueSchedule(sha);
                }
            }
        }
        _lastReverify = new(DateTimeOffset.UtcNow, checkedCount, failed);
    }
}
