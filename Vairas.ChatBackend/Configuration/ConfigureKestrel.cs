namespace Vairas.ChatBackend.Configuration;

public static class ConfigureKestrel
{
    /// <summary>No server header and a 64 KiB request body limit: chat messages are short text.</summary>
    public static IWebHostBuilder AddHardenedKestrel(this IWebHostBuilder webHost) =>
        webHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 65536;
        });
}
