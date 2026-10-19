using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using DistributedHls.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DistributedHls;

// Worker protocol 2. No global lock: every transition is a conditional update inside the store.
[ApiController]
[Authorize(Policy = "DistributedHls.Worker")]
[Route("Plugins/DistributedHls/Jobs")]
[RequireWorkerProtocol]
public sealed class WorkerController(JobStore store, Coordinator coordinator, PublishedIndex index, AccessPolicy policy, VerificationService verification,
    ILibraryManager library, ILogger<WorkerController> logger) : ControllerBase
{
    [HttpGet("Protocol")]
    public IActionResult Protocol() => Guard(() => Ok(new { Version = 2 }));

    [HttpPost("Poll")]
    public IActionResult Poll(PollRequest input) => Guard(() =>
    {
        if (string.IsNullOrWhiteSpace(input.Worker) || input.Worker.Length > 128) return BadRequest("Worker 名称长度应为 1–128。");
        var config = Plugin.Instance.Configuration;
        if (!config.Enabled) return NoContent();
        var now = DateTimeOffset.UtcNow;
        string? sha = null;
        if (input.Sha256 is not null)
        {
            // A worker that already knows the hash: register what it reports, then claim only that hash.
            sha = ProbeReader.NormalizeSha256(input.Sha256);
            var probe = input.ProbeJson is null ? null : ProbeReader.Parse(input.ProbeJson);
            var media = store.GetMedia(sha);
            if (media is null)
            {
                if (probe is null) return BadRequest("未知 SHA256 需要同时提供 FFprobe 信息");
                if (input.ItemId is { } itemId)
                {
                    if (AssociateItem(itemId, sha, probe, now) is { } refused) return refused;
                }
                else
                {
                    // A file outside the library: it starts as an orphan of the given library.
                    if (input.LibraryId is not { } libraryId || !policy.LibraryEligible(libraryId)) return BadRequest("新素材必须指定已启用 HLS 的媒体库");
                    store.AssociateProbe(null, sha, probe, libraryId, now);
                }
            }
            else
            {
                // Only compares: a different probe for a known hash is refused.
                if (probe is not null) store.AssociateProbe(null, sha, probe, media.LibraryId, now);
                if (input.ItemId is { } itemId && AssociateItem(itemId, sha, media.Probe, now) is { } refused) return refused;
            }
            // This worker asks for the hash, so it is scheduled even when no library file references it.
            coordinator.SyncAndSchedule(sha, includeUnreferenced: true);
        }
        else if (input.ProbeJson is not null) return BadRequest("FFprobe 信息必须与 SHA256 一起提供");
        var lease = store.Claim(new ClaimRequest(input.Worker, coordinator.LeaseDuration, sha, config.OutputPath,
            m => RenditionPlanner.Desired(m.Probe, config.Qualities), coordinator.IsShaEligible, now));
        if (lease is null) return NoContent();
        // Relative to the server base address, so a reverse proxy prefix is kept by the worker.
        var download = lease.Kind == JobKind.Probe || JsonSerializer.Deserialize<EncodePlan>(lease.Payload)?.Source is not null
            ? $"Plugins/DistributedHls/Jobs/{lease.Id:N}/Input?leaseToken={lease.LeaseToken}" : null;
        return Ok(new WorkAssignment(lease, download));
    });

    [HttpGet("{id:guid}/Input")]
    public IActionResult Input(Guid id, [Required] string leaseToken) => Guard(() =>
    {
        var lease = store.GetLease(id, leaseToken, DateTimeOffset.UtcNow);
        if (lease is null) return Conflict("任务租约已失效。");
        // An encode job reads the file its plan chose, which may belong to another item with the same hash.
        var file = lease.Kind == JobKind.Probe ? JsonSerializer.Deserialize<SourceFile>(lease.Payload) : JsonSerializer.Deserialize<EncodePlan>(lease.Payload)?.Source;
        if (file is null) return NotFound("任务没有可下载的输入。");
        if (store.GetSource(file.ItemId)?.File.Path != file.Path) return Conflict("素材已变化");
        // Inside a public S3 mapping the S3 plugin turns this into a redirect to the storage.
        return PhysicalFile(file.Path, "application/octet-stream", enableRangeProcessing: true);
    });

    [HttpPost("{id:guid}/Heartbeat")]
    public IActionResult Heartbeat(Guid id, LeaseRequest input) => Guard(() =>
        store.Heartbeat(id, input.LeaseToken, coordinator.LeaseDuration, DateTimeOffset.UtcNow) ? NoContent() : Conflict("任务租约已失效。"));

    [HttpPost("{id:guid}/Probe")]
    public IActionResult Probe(Guid id, ProbeCompletion input) => Guard(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var lease = store.GetLease(id, input.LeaseToken, now);
        if (lease is null || lease.Kind != JobKind.Probe) return Conflict("任务租约已失效。");
        var file = JsonSerializer.Deserialize<SourceFile>(lease.Payload) ?? throw new JsonException("探测任务缺少素材信息。");
        var stored = store.GetSource(file.ItemId);
        var current = stored?.File;
        if (current is null || current.Path != file.Path || current.Length != file.Length || current.LastWriteTimeUtc.Ticks != file.LastWriteTimeUtc.Ticks)
        {
            store.CancelJob(id, now);
            return Conflict("素材已变化，任务已取消");
        }
        var sha = ProbeReader.NormalizeSha256(input.Sha256);
        var probe = ProbeReader.Parse(input.ProbeJson);
        // A probe the server dispatched keeps the stored probe of a known hash. Another FFprobe build may describe the same
        // file differently; refusing it would leave the item without HLS for good. A probe a worker submits (Poll) is still compared.
        var media = store.AssociateProbe(file.ItemId, sha, probe, current.LibraryId, now, keepExistingProbe: true);
        if (JsonSerializer.Serialize(media.Probe) != JsonSerializer.Serialize(probe))
            logger.LogWarning("SHA256 {Sha256} 已有媒体信息，与本次探测结果不同（可能是不同版本的 FFprobe），沿用已保存的信息", sha);
        // An item a poll associated with another hash meanwhile leaves that hash, which may have lost its last file.
        if (stored?.Sha256 is { } previous && previous != sha) store.CancelUnreferencedEncodeJob(previous, now);
        // The association is committed whatever the lease check below says, so the index and the schedule follow it either way.
        index.RefreshItems([file.ItemId]);
        var completed = store.CompleteProbe(id, input.LeaseToken, now);
        coordinator.SyncAndSchedule(sha);
        return completed ? NoContent() : Conflict("任务租约已失效。");
    });

    // Only records the manifest; the verification service publishes it once every file is in place.
    [HttpPost("{id:guid}/Complete")]
    public IActionResult Complete(Guid id, EncodeCompletion input) => Guard(() =>
    {
        var now = DateTimeOffset.UtcNow;
        var lease = store.GetLease(id, input.LeaseToken, now);
        if (lease is null || lease.Kind != JobKind.Encode) return Conflict("任务租约已失效。");
        var plan = JsonSerializer.Deserialize<EncodePlan>(lease.Payload) ?? throw new JsonException("编码任务缺少计划。");
        AssetPaths.ValidateCompletion(plan, input);
        if (!store.SubmitOutputs(id, input.LeaseToken, plan, input, now)) return Conflict("任务租约已失效。");
        verification.Signal();
        return Accepted();
    });

    [HttpPost("{id:guid}/Failed")]
    public IActionResult Failed(Guid id, FailureRequest input) => Guard(() =>
        store.Release(id, input.LeaseToken, input.Error[..Math.Min(input.Error.Length, 2048)], Plugin.Instance.Configuration.MaxAttempts, DateTimeOffset.UtcNow)
            ? NoContent() : Conflict("任务租约已失效。"));

    // Every action answers a malformed request with 400 and the reason, and 503 while the database is unavailable.
    private IActionResult Guard(Func<IActionResult> action)
    {
        if (store.InitError is not null) return StatusCode(StatusCodes.Status503ServiceUnavailable, "HLS 数据库不可用：" + store.InitError);
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or KeyNotFoundException)
        {
            return BadRequest(ex.Message);
        }
    }

    // Null once the item carries the hash, otherwise the answer to return.
    private IActionResult? AssociateItem(Guid itemId, string sha, ProbeInfo probe, DateTimeOffset now)
    {
        if (library.GetItemById(itemId) is not Video video || !coordinator.IsCandidate(video, out var libraryId)) return BadRequest("条目不存在，或不在启用 HLS 的媒体库中。");
        if (store.GetSource(itemId) is not { } source)
        {
            coordinator.EnqueueItem(itemId);
            return Conflict("等待媒体库同步后重试");
        }
        store.AssociateProbe(itemId, sha, probe, libraryId, now);
        // The hash the item carried until now may have lost its last file.
        if (source.Sha256 is { } previous && previous != sha) store.CancelUnreferencedEncodeJob(previous, now);
        index.RefreshItems([itemId]);
        return null;
    }
}
