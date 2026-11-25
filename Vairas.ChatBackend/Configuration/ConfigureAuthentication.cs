using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Vairas.ChatBackend.Configuration.Options;

namespace Vairas.ChatBackend.Configuration;

/// <summary>Bearer validation for user tokens and internal workload tokens.</summary>
public static class ConfigureAuthentication
{
    public static IServiceCollection AddBearerAuthentication(this IServiceCollection services)
    {
        var authentication = services.AddAuthentication("User");
        foreach (var scheme in new[] { "User", "Workload" })
        {
            var user = scheme == "User";
            authentication.AddJwtBearer(scheme, _ => { });
            services.AddOptions<JwtBearerOptions>(scheme).Configure<IOptions<JwtOptions>>((options, jwt) =>
            {
                var settings = jwt.Value;
                options.Authority = settings.Authority; options.MapInboundClaims = false; options.RequireHttpsMetadata = true; options.IncludeErrorDetails = false;
                options.TokenValidationParameters = new() { ValidateIssuer = true, ValidIssuer = settings.Issuer, ValidateAudience = true, ValidAudience = user ? "vairas-chat" : "vairas-chat-internal", ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidateLifetime = true, RequireExpirationTime = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ClockSkew = TimeSpan.FromSeconds(30) };
                options.Events = new()
                {
                    OnTokenValidated = context =>
                    {
                        if (user && (!Id(context.Principal!, "sub") || !Id(context.Principal!, "sid"))) context.Fail("Invalid user binding.");
                        return Task.CompletedTask;
                    },
                    OnChallenge = context => { context.HandleResponse(); context.Response.Headers.WWWAuthenticate = "Bearer"; return Problems.WriteAsync(context.HttpContext, 401, "AUTHENTICATION_REQUIRED"); },
                    OnForbidden = context => Problems.WriteAsync(context.HttpContext, 403, "ACCESS_DENIED")
                };
            });
        }
        return services;
    }

    private static bool Id(ClaimsPrincipal user, string claim) => user.FindAll(claim).ToArray() is { Length: 1 } values && Guid.TryParse(values[0].Value, out var id) && id != Guid.Empty;
}
