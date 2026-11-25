using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ChatBackend.Integrations;
using Vairas.ChatBackend.Configuration.Options;
namespace Vairas.ChatBackend.Startup;

public static class SyntheticModelProbe
{
    public static async Task RunAsync()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var options = configuration.GetSection("Model").Get<ModelOptions>() ?? new();
        if (configuration["VAIRAS_CHAT_LIVE_PROBE"] != "synthetic-only" || !options.Enabled || !options.AllowSyntheticCloud || options.RealPersonalDataEnabled || !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || string.IsNullOrWhiteSpace(options.Deployment) || string.IsNullOrWhiteSpace(options.Region))
            throw new InvalidOperationException("Explicit synthetic-only probe and HTTPS model configuration required.");
        using var adapter = new AzureModelAdapter(options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var deployment = new ModelDeployment { Provider = "AzureOpenAI", DeploymentName = options.Deployment, Region = options.Region, EndpointConfigKey = "Model:Endpoint", CredentialSecretRef = "Model:Credential" };
        var result = await adapter.GenerateAsync(deployment, [new ModelMessage("user", "Synthetic test: reply with a short greeting. No personal data, school or action is involved.")], [], deadline.Token);
        if (string.IsNullOrWhiteSpace(result.Text) || result.Calls.Count != 0) throw new InvalidOperationException("Unexpected probe response.");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { syntheticCloudProbe = "PASSED", result.InputTokens, result.OutputTokens, businessDispatch = false }));
    }
}
