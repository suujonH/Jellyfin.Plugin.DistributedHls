using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.DistributedHls;

// Workers send the header on every request. Runs before [ApiController] model validation (order -2000), so a worker of another
// protocol always gets this answer rather than a model error.
public sealed class RequireWorkerProtocolAttribute : ActionFilterAttribute
{
    public const string Header = "X-DistributedHls-Protocol";

    public RequireWorkerProtocolAttribute() => Order = -3000;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.Request.Headers[Header] != "2") context.Result = new BadRequestObjectResult("Worker 协议版本不匹配：需要 2");
    }
}
