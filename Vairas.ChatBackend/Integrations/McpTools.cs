using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ChatBackend.Configuration.Options;
namespace Vairas.ChatBackend.Integrations;


public sealed class McpTools(IHttpClientFactory clients, DependencyOptions dependencies, McpOptions options, IHttpContextAccessor context) : IMcpTools
{
    public async Task<JsonElement> CallAsync(string name, JsonElement arguments, Guid school, ChatActor actor, CancellationToken ct)
    {
        _ = ToolCatalog.Definition(name); arguments = ToolCatalog.Normalize(name, arguments, school);
        if (!options.Enabled) throw ChatErrors.Dependency("MCP_DISABLED");
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) throw ChatErrors.Dependency("MCP_CONFIGURATION_INVALID");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            const string grant = "urn:ietf:params:oauth:grant-type:token-exchange"; const string type = "urn:ietf:params:oauth:token-type:access_token";
            var scopes = "mcp.context.read mcp." + name;
            using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(dependencies.AuthBaseUrl), "connect/token"))
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = grant, ["client_id"] = dependencies.ClientId, ["client_secret"] = dependencies.ClientSecret, ["subject_token"] = actor.UserBearer, ["subject_token_type"] = type, ["resource"] = options.Endpoint, ["scope"] = scopes, ["school_id"] = school.ToString("D") }) };
            using var tokenResponse = await clients.CreateClient("owners").SendAsync(tokenRequest, deadline.Token);
            if (!tokenResponse.IsSuccessStatusCode) throw ChatErrors.Dependency("DELEGATION_REJECTED");
            var token = await tokenResponse.Content.ReadFromJsonAsync<TokenReply>(cancellationToken: deadline.Token);
            if (token is null || string.IsNullOrWhiteSpace(token.AccessToken) || token.TokenType != "Bearer" || token.IssuedTokenType != type || token.ExpiresIn is < 1 or > 60 || !scopes.Split(' ').Order().SequenceEqual(token.Scope.Split(' ').Order())) throw ChatErrors.Dependency("DELEGATION_REJECTED");
            using var http = clients.CreateClient("mcp");
            await using var transport = new HttpClientTransport(new() { Endpoint = endpoint, EnableStandaloneGetStream = false, AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token.AccessToken, ["X-Correlation-ID"] = context.HttpContext?.Items["CorrelationId"]?.ToString() ?? Guid.NewGuid().ToString("D") } }, http, ownsHttpClient: false);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
            var catalog = await client.ListToolsAsync(cancellationToken: deadline.Token);
            if (catalog.Count != ToolCatalog.All.Count || catalog.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Count) throw ChatErrors.Dependency("MCP_CONTRACT_MISMATCH");
            foreach (var item in catalog)
            {
                var expected = ToolCatalog.All.SingleOrDefault(t => t.Name == item.Name);
                if (expected is null || ChatRules.Hash(expected.Schema) != ChatRules.Hash(item.ProtocolTool.InputSchema) || item.ProtocolTool.Meta?["toolContractVersion"]?.GetValue<string>() != "chat-tools/1" || item.ProtocolTool.Meta?["executionProfile"]?.GetValue<string>() != "PreviewOnlyV1" || item.ProtocolTool.Annotations?.ReadOnlyHint != true || item.ProtocolTool.OutputSchema is not JsonElement outputSchema || ChatRules.Hash(outputSchema) != ChatRules.Hash(OutputContracts.Schema(item.Name))) throw ChatErrors.Dependency("MCP_CONTRACT_MISMATCH");
            }
            var result = await client.CallToolAsync(name, arguments.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value), cancellationToken: deadline.Token);
            if (result.IsError == true)
            {
                var code = "MCP_TOOL_REJECTED";
                var text = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault()?.Text;
                if (text is not null && text.Length <= 1024) { using var error = JsonDocument.Parse(text); var candidate = error.RootElement.GetProperty("code").GetString(); if (candidate is not null && System.Text.RegularExpressions.Regex.IsMatch(candidate, "^[A-Z_]{1,64}$")) code = candidate; }
                throw code switch { "RESOURCE_NOT_AVAILABLE" => ChatErrors.Missing(), "SESSION_INVALID" => new UnauthorizedException(code), "PROFILE_INACTIVE" => new ForbiddenException(code), "OWNER_CONFLICT" => ChatErrors.Conflict(code), "OWNER_INPUT_REJECTED" => new UnprocessableEntityException(code), _ => ChatErrors.Dependency(code) };
            }
            if (result.StructuredContent is not JsonElement structured || structured.ValueKind != JsonValueKind.Object || !OutputContracts.Valid(name, structured) || System.Text.Encoding.UTF8.GetByteCount(structured.GetRawText()) > 32768) throw ChatErrors.Dependency("MCP_RESPONSE_INVALID");
            return structured.Clone();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw ChatErrors.Dependency("MCP_TIMEOUT"); }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or ModelContextProtocol.McpException) { throw ChatErrors.Dependency("MCP_UNAVAILABLE"); }
    }
    private sealed record TokenReply([property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken, [property: System.Text.Json.Serialization.JsonPropertyName("issued_token_type")] string IssuedTokenType, [property: System.Text.Json.Serialization.JsonPropertyName("token_type")] string TokenType, [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn, string Scope);
}
