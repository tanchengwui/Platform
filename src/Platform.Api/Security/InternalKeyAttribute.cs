using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Platform.Api.Security;

/// <summary>
/// Simple internal API-key auth for Runtime -> Api calls.
/// Configure: InternalApiKey (or reuse InternalSyncKey).
/// Runtime must send header: X-Platform-Internal-Key.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class InternalKeyAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var cfg = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = cfg.GetValue<string>("InternalApiKey") ?? cfg.GetValue<string>("InternalSyncKey");

        if (string.IsNullOrWhiteSpace(expected))
        {
            context.Result = new UnauthorizedObjectResult("Internal API key not configured.");
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue("X-Platform-Internal-Key", out var got) || got != expected)
        {
            context.Result = new UnauthorizedObjectResult("Invalid internal API key.");
            return;
        }

        await next();
    }
}
