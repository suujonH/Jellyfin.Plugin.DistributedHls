using DistributedHls.Core;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DistributedHls;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/DistributedHls")]
public sealed class AdminController(JobStore store, Coordinator coordinator, VerificationService verification, ContractCheck contract, OutputRootMonitor monitor)
    : ControllerBase
{
    [HttpGet("Jobs")]
    public IActionResult Jobs(int limit = 200) => store.InitError is not null
        ? StatusCode(StatusCodes.Status503ServiceUnavailable, "HLS 数据库不可用：" + store.InitError)
        : Ok(store.List(Math.Clamp(limit, 1, 1000)));

    // Without the database there is nothing to count, only StoreError. OutputRootProblem is why verification and deletion are paused.
    [HttpGet("Status")]
    public IActionResult Status()
    {
        var counts = store.InitError is null ? store.Counts() : null;
        return Ok(new
        {
            Counts = counts, PendingDeletions = counts?.PendingDeletions, contract.Problems, verification.LastReverify, OutputRootProblem = monitor.Problem,
            StoreError = store.InitError
        });
    }

    // Rebuilds the work list on the next coordinator pass; published outputs are kept.
    [HttpPost("Rebuild")]
    public IActionResult Rebuild()
    {
        coordinator.RequestRebuild();
        return Accepted();
    }

    [HttpPost("Verify")]
    public IActionResult Verify(Guid? itemId) => verification.StartReverify(itemId) ? Accepted() : Conflict("已有一次校验正在运行。");
}
