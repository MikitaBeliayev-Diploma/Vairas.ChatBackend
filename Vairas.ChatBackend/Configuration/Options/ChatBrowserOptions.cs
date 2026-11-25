namespace Vairas.ChatBackend.Configuration.Options;

/// <summary>Bound from "Chat": browser origins trusted to confirm and execute assistant action previews.</summary>
public sealed class ChatBrowserOptions
{
    public string[] BrowserOrigins { get; set; } = [];
}
