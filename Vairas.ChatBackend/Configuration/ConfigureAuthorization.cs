using System.Security.Claims;
using Microsoft.Extensions.Options;
using Vairas.ChatBackend.Configuration.Options;

namespace Vairas.ChatBackend.Configuration;

public static class ConfigureAuthorization
{
    public static IServiceCollection AddChatAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder().AddPolicy("lesson.authorize", p => p.AddAuthenticationSchemes("Workload").RequireAuthenticatedUser().RequireAssertion(c =>
        {
            var caller = c.User.FindFirstValue("sub");
            var settings = (c.Resource as HttpContext)?.RequestServices.GetRequiredService<IOptions<JwtOptions>>().Value;
            const string scope = Vairas.ConfirmedActions.LessonActions.AuthorizationScope;
            return caller == "scheduling" && settings is not null && settings.WorkloadClients.TryGetValue(caller, out var scopes) && scopes.Contains(scope, StringComparer.Ordinal) && c.User.FindAll("scope").SelectMany(s => s.Value.Split(' ')).Contains(scope, StringComparer.Ordinal);
        })).AddPolicy("user", p => p.AddAuthenticationSchemes("User").RequireAuthenticatedUser())
            .AddPolicy("confirmation.read", p => p.AddAuthenticationSchemes("Workload").RequireAuthenticatedUser().RequireAssertion(c =>
            {
                var caller = c.User.FindFirstValue("sub");
                var settings = (c.Resource as HttpContext)?.RequestServices.GetRequiredService<IOptions<JwtOptions>>().Value;
                return caller is not null && settings is not null && settings.WorkloadClients.TryGetValue(caller, out var scopes) && scopes.Contains("confirmations.validate", StringComparer.Ordinal) && c.User.FindAll("scope").SelectMany(s => s.Value.Split(' ')).Contains("confirmations.validate", StringComparer.Ordinal);
            }));
        return services;
    }
}
