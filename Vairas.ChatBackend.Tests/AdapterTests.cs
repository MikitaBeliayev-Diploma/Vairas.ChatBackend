using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using System.ClientModel.Primitives;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ChatBackend.Integrations;
using Xunit;
namespace Vairas.ChatBackend.Tests;

[Trait("Category", "Adapter")]
public sealed class AdapterTests
{
    private sealed class WireHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Count++; return respond(request); }
    }
    private sealed class Clients(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Tokens : IWorkloadTokens { public Task<string> GetAsync(CancellationToken ct) => Task.FromResult("synthetic-workload-token"); }
    private static HttpResponseMessage Reply(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    [Fact]
    public async Task Disabled_MCP_has_no_direct_REST_fallback_and_SDK_envelopes_are_bounded()
    {
        using var handler = new WireHandler(_ => Task.FromResult(Reply(new string('x', 524289)))); using var client = new HttpClient(handler);
        var tools = new McpTools(new Clients(client), new(), new(), new HttpContextAccessor());
        var actor = new ChatActor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic-user", DateTimeOffset.UtcNow.AddSeconds(30));
        var disabled = await Business.ThrowsBusinessAsync(() => tools.CallAsync("get_my_enrollments", Wire.Element(new { }), Guid.NewGuid(), actor, default)); Assert.Equal("MCP_DISABLED", disabled.Code); Assert.Equal(0, handler.Count);
        using var limited = new HttpClient(new McpResponseLimit { InnerHandler = handler });
        var oversized = await Business.ThrowsBusinessAsync(() => limited.GetAsync("https://localhost:7900/mcp")); Assert.Equal("MCP_RESPONSE_TOO_LARGE", oversized.Code);
    }

    [Fact]
    public async Task Real_Azure_SDK_wire_adapter_advertises_only_read_preview_tools_and_decodes_calls()
    {
        string? body = null;
        using var handler = new WireHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("/openai/deployments/synthetic/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-api-key", request.Headers.GetValues("api-key").Single());
            return Reply("""{"id":"chatcmpl-fixture","object":"chat.completion","created":1700000000,"model":"synthetic","choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_my_enrollments","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}""");
        });
        using var http = new HttpClient(handler);
        var azure = new AzureOpenAIClient(new Uri("https://synthetic.openai.azure.com/"), new AzureKeyCredential("synthetic-api-key"), new AzureOpenAIClientOptions { Transport = new HttpClientPipelineTransport(http), RetryPolicy = new ClientRetryPolicy(0) });
        using var client = azure.GetChatClient("synthetic").AsIChatClient();
        var turn = await AzureModelAdapter.GenerateWithAsync(client, [new("user", "Read synthetic enrollments")], ToolCatalog.All, default);
        Assert.Single(turn.Calls); Assert.Equal("get_my_enrollments", turn.Calls[0].Name); Assert.Equal(3, turn.InputTokens); Assert.Equal(4, turn.OutputTokens);
        using var requestBody = JsonDocument.Parse(body!);
        var tools = requestBody.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()).ToArray();
        Assert.Equal(6, tools.Length); Assert.DoesNotContain("create_lesson_booking", tools); Assert.DoesNotContain("confirm", tools);
        Assert.DoesNotContain("synthetic-workload-token", body); Assert.Equal(1, handler.Count);
    }
    [Fact]
    public async Task Model_configuration_never_enables_unapproved_personal_data_or_implicit_provider_fallback()
    {
        using var adapter = new AzureModelAdapter(new() { Enabled = false });
        Assert.False(adapter.Enabled);
        await Business.ThrowsBusinessAsync(() => adapter.GenerateAsync(new(), [], ToolCatalog.All, default));
    }
    [Fact]
    public async Task OAuth_uses_wire_names_and_caches_only_a_complete_scoped_token()
    {
        using var handler = new WireHandler(async request =>
        {
            var form = await request.Content!.ReadAsStringAsync(); Assert.Contains("grant_type=client_credentials", form); Assert.Contains("client_secret=synthetic-secret", form);
            return Reply("{\"access_token\":\"synthetic-scoped-token\",\"token_type\":\"Bearer\",\"expires_in\":900,\"scope\":\"identity.validate school.assistant.read\"}");
        });
        using var client = new HttpClient(handler); using var tokens = new WorkloadTokens(new Clients(client), new() { ClientSecret = "synthetic-secret" }, TimeProvider.System);
        Assert.Equal("synthetic-scoped-token", await tokens.GetAsync(default)); Assert.Equal("synthetic-scoped-token", await tokens.GetAsync(default)); Assert.Equal(1, handler.Count);
    }
    [Fact]
    public async Task Owner_transport_preserves_bearer_and_correlation_and_never_follows_business_mutations()
    {
        var requests = new List<string>(); var school = Guid.NewGuid();
        using var handler = new WireHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery); Assert.Equal("synthetic-user", request.Headers.Authorization!.Parameter);
            Assert.Equal("synthetic-correlation", request.Headers.GetValues("X-Correlation-ID").Single());
            return Task.FromResult(Reply(JsonSerializer.Serialize(new[] { new { schoolId = school, enrollmentId = Guid.NewGuid() }, new { schoolId = Guid.NewGuid(), enrollmentId = Guid.NewGuid() } })));
        });
        using var client = new HttpClient(handler); var context = new DefaultHttpContext(); context.Items["CorrelationId"] = "synthetic-correlation";
        var transport = new OwnerTransport(new Clients(client), new Tokens(), new HttpContextAccessor { HttpContext = context }); var tools = new McpTools(new Clients(client), new(), new() { Enabled = false }, new HttpContextAccessor { HttpContext = context });
        var actor = new ChatActor(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic-user", DateTimeOffset.UtcNow.AddSeconds(30));
        var result = await transport.SendAsync("https://localhost:7000/", "api/v1/users/me/enrollments", HttpMethod.Get, null, actor.UserBearer, default);
        Assert.Equal(2, result.GetArrayLength()); Assert.Equal("/api/v1/users/me/enrollments", requests.Single());
        await Business.ThrowsBusinessAsync(() => tools.CallAsync("http_request", Wire.Element(new { }), school, actor, default));
        Assert.Single(requests);
    }
    [Theory]
    [InlineData(401, "SESSION_INVALID")][InlineData(403, "RESOURCE_NOT_AVAILABLE")][InlineData(404, "RESOURCE_NOT_AVAILABLE")][InlineData(409, "OWNER_CONFLICT")][InlineData(500, "DEPENDENCY_UNAVAILABLE")]
    public async Task Owner_errors_are_minimized_without_echoing_private_bodies(int status, string code)
    {
        using var handler = new WireHandler(_ => Task.FromResult(Reply("{\"detail\":\"private-owner-detail\"}", (HttpStatusCode)status))); using var client = new HttpClient(handler);
        var transport = new OwnerTransport(new Clients(client), new Tokens(), new HttpContextAccessor());
        var error = await Business.ThrowsBusinessAsync(() => transport.SendAsync("https://localhost:7100", "safe", HttpMethod.Get, null, null, default));
        Assert.Equal(code, error.Code); Assert.DoesNotContain("private-owner-detail", error.Message);
    }
}
