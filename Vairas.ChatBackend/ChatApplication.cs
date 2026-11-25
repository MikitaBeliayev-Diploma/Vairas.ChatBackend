using Vairas.ChatBackend.Configuration;

namespace Vairas.ChatBackend;

/// <summary>Composition root. Tests build the same host through <see cref="Build"/> and may adjust the builder first.</summary>
public static class ChatApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);

        builder.WebHost.AddHardenedKestrel();
        builder.Logging.AddJsonLogging();
        builder.Services
            .AddChatOptions(builder.Configuration)
            .AddStrictJsonSerialization()
            .AddChatDatabase()
            .AddChatServices()
            .AddOwnerHttpClients()
            .AddBearerAuthentication()
            .AddChatAuthorization()
            .AddMessageRateLimiting()
            .AddServiceHealthChecks()
            .AddOpenApiDocument()
            .AddServiceTelemetry(builder.Configuration);

        var app = builder.Build();
        app.UseMiddleware<Boundary>();
        app.UseRouting();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseMiddleware<ConfirmationOriginMiddleware>();
        app.MapChatEndpoints();
        app.MapServiceHealthChecks();
        app.MapOpenApiDocument();
        return app;
    }
}
