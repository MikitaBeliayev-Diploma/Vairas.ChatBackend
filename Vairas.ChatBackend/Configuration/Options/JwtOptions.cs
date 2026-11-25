namespace Vairas.ChatBackend.Configuration.Options;

/// <summary>Bound from "Authentication": token validation settings for user and workload callers.</summary>
public sealed class JwtOptions
{
    public string Authority { get; set; } = "https://localhost:7100";
    public string Issuer { get; set; } = "https://localhost:7100";
    public Dictionary<string, string[]> WorkloadClients { get; set; } = [];
}
