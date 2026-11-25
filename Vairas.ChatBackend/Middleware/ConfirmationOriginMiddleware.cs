using Microsoft.Extensions.Options;
using Vairas.ChatBackend.Configuration.Options;

namespace Vairas.ChatBackend;

/// <summary>Confirmation and execution of an action preview must come from exactly one trusted browser origin.</summary>
public sealed class ConfirmationOriginMiddleware(RequestDelegate next, IOptions<ChatBrowserOptions> browser)
{
    private readonly ChatBrowserOptions options = browser.Value;

    public async Task InvokeAsync(HttpContext http)
    {
        if (HttpMethods.IsPost(http.Request.Method) && http.Request.Path.StartsWithSegments("/api/v1/chat/action-previews") && (http.Request.Path.Value!.EndsWith("/confirmations", StringComparison.OrdinalIgnoreCase) || http.Request.Path.Value!.EndsWith("/executions", StringComparison.OrdinalIgnoreCase)))
        {
            var origin = http.Request.Headers.Origin;
            if (origin.Count != 1 || !options.BrowserOrigins.Contains(origin[0], StringComparer.Ordinal)) { await Problems.WriteAsync(http, 403, "TRUSTED_CONFIRMATION_ORIGIN_REQUIRED"); return; }
        }
        await next(http);
    }
}
