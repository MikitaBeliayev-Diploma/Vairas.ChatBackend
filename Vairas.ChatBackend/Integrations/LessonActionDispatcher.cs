using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ConfirmedActions;
using Vairas.ChatBackend.Configuration.Options;
namespace Vairas.ChatBackend.Integrations;

public sealed class LessonActionDispatcher(IHttpClientFactory clients, DependencyOptions dependencies, McpOptions options) : ILessonActionDispatcher
{
    public Task<LessonActionReceipt> ExecuteAsync(LessonAction action, ChatActor actor, CancellationToken ct) => CallAsync(action, actor, ct);
    public Task<LessonActionReceipt> ResolveAsync(LessonAction action, CancellationToken ct) => CallAsync(action, null, ct);
    private async Task<LessonActionReceipt> CallAsync(LessonAction action, ChatActor? actor, CancellationToken ct)
    {
        LessonActions.Validate(action);
        if (actor is not null && !options.Enabled) throw ChatErrors.Dependency("MCP_DISABLED");
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) throw ChatErrors.Dependency("MCP_CONFIGURATION_INVALID");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var fields = new Dictionary<string, string> { ["client_id"] = dependencies.ClientId, ["client_secret"] = dependencies.ClientSecret };
        if (actor is null) { fields["grant_type"] = "client_credentials"; fields["scope"] = LessonActions.RecoveryScope; }
        else
        {
            fields["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange"; fields["subject_token"] = actor.UserBearer;
            fields["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token"; fields["resource"] = options.Endpoint;
            fields["scope"] = LessonActions.Scope(action.Binding.Kind); fields["school_id"] = action.Binding.SchoolId.ToString("D");
            fields["lesson_action"] = JsonSerializer.Serialize(action.Binding, LessonActions.Json);
        }
        using var response = await clients.CreateClient("owners").PostAsync(new Uri(new Uri(dependencies.AuthBaseUrl), "connect/token"), new FormUrlEncodedContent(fields), deadline.Token);
        if (!response.IsSuccessStatusCode) throw ChatErrors.Dependency("DELEGATION_REJECTED");
        using var tokenJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token)); var token = tokenJson.RootElement;
        if (token.GetProperty("token_type").GetString() != "Bearer" || token.GetProperty("scope").GetString() != fields["scope"] || token.GetProperty("expires_in").GetInt32() is < 1 or > 3600) throw ChatErrors.Dependency("DELEGATION_REJECTED");
        if (actor is not null && (token.GetProperty("expires_in").GetInt32() > 60 || token.GetProperty("issued_token_type").GetString() != "urn:ietf:params:oauth:token-type:access_token")) throw ChatErrors.Dependency("DELEGATION_REJECTED");
        using var http = clients.CreateClient("mcp");
        await using var transport = new HttpClientTransport(new() { Endpoint = endpoint, EnableStandaloneGetStream = false, AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token.GetProperty("access_token").GetString(), ["X-Correlation-ID"] = Guid.NewGuid().ToString("D") } }, http, ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        var tool = actor is null ? "resolve_lesson_action" : LessonActions.Tool(action.Binding.Kind);
        var catalog = await client.ListToolsAsync(cancellationToken: deadline.Token);
        if (catalog.Count != 1 || catalog[0].Name != tool || catalog[0].ProtocolTool.Meta?["toolContractVersion"]?.GetValue<string>() != "confirmed-lesson-actions/1" || catalog[0].ProtocolTool.Annotations?.ReadOnlyHint != false) throw ChatErrors.Dependency("MCP_CONTRACT_MISMATCH");
        if (catalog[0].ProtocolTool.Meta?["executionProfile"]?.GetValue<string>() != LessonActions.Profile || LessonActions.Hash(catalog[0].ProtocolTool.InputSchema) != LessonActions.Hash(LessonActions.InputSchema) || catalog[0].ProtocolTool.OutputSchema is not JsonElement outputSchema || LessonActions.Hash(outputSchema) != LessonActions.Hash(LessonActions.OutputSchema)) throw ChatErrors.Dependency("MCP_CONTRACT_MISMATCH");
        var payload = JsonSerializer.SerializeToElement(action, LessonActions.Json);
        var result = await client.CallToolAsync(tool, payload.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value), cancellationToken: deadline.Token);
        if (result.IsError == true || result.StructuredContent is not JsonElement structured || structured.GetRawText().Length > 32768) throw ChatErrors.Dependency("EXECUTION_OUTCOME_UNKNOWN");
        return structured.Deserialize<LessonActionReceipt>(LessonActions.Json) ?? throw ChatErrors.Dependency("EXECUTION_OUTCOME_UNKNOWN");
    }
}
