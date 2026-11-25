using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Vairas.ChatBackend.Configuration;

public static class ConfigureRateLimiting
{
    public static IServiceCollection AddMessageRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("messages", http => RateLimitPartition.GetFixedWindowLimiter(http.User.FindFirstValue("sub") ?? "unknown", _ => new() { PermitLimit = 20, QueueLimit = 0, Window = TimeSpan.FromMinutes(1) }));
            options.OnRejected = (context, _) => { context.HttpContext.Response.Headers.RetryAfter = "60"; return new ValueTask(Problems.WriteAsync(context.HttpContext, 429, "RATE_LIMITED")); };
        });
        return services;
    }
}
