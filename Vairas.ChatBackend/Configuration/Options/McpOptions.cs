namespace Vairas.ChatBackend.Configuration.Options;

public sealed class McpOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "https://localhost:7900/mcp";
}
