namespace Vairas.ChatBackend.Domain;

public sealed class ModelDeployment
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = "";
    public string ModelName { get; set; } = "";
    public string? ModelVersion { get; set; }
    public string DeploymentName { get; set; } = "";
    public string Region { get; set; } = "";
    public string EndpointConfigKey { get; set; } = "";
    public string CredentialSecretRef { get; set; } = "";
    public int ConfigurationVersion { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
