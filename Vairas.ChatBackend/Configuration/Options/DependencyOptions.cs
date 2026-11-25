namespace Vairas.ChatBackend.Configuration.Options;

public sealed class DependencyOptions
{
    public string AuthBaseUrl { get; set; } = "https://localhost:7100/";
    public string SchoolBaseUrl { get; set; } = "https://localhost:7200/";
    public string BusinessApiBaseUrl { get; set; } = "https://localhost:7000/";
    public string ClientId { get; set; } = "chat-backend";
    public string ClientSecret { get; set; } = "";
    public string Scope { get; set; } = "identity.validate school.assistant.read";
}
