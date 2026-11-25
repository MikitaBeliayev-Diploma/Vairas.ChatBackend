namespace Vairas.ChatBackend.Configuration.Options;

public sealed class ModelOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string Deployment { get; set; } = "";
    public string Region { get; set; } = "";
    public string? ApiKey { get; set; }
    public bool AllowSyntheticCloud { get; set; }
    public bool RealPersonalDataEnabled { get; set; }
}
