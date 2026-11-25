using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Vairas.ChatBackend.Business;
using Xunit;

namespace Vairas.ChatBackend.Tests;

[Trait("Category", "Http")]
public sealed class HttpTests
{
    private static HttpRequestMessage Request(HttpMethod method, string path, string? token = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        if (body is not null) { request.Content = JsonContent.Create(body, options: Wire.Json); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString()); }
        return request;
    }
    private static async Task<Guid> Session(ChatFactory factory, HttpClient client)
    {
        using var request = Request(HttpMethod.Post, "/api/v1/chat/sessions", factory.Token(), new { schoolId = factory.World.School });
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.NotNull(response.Headers.Location);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
    }
    [Fact]
    public async Task Real_HTTP_pipeline_saves_history_preview_and_first_party_approval_without_dispatch()
    {
        await using var factory = new ChatFactory(); using var client = factory.Client(); var school = factory.World.School; var conversation = await Session(factory, client);
        factory.World.Model.Turns.Enqueue(new(null, [new("preview", "preview_lesson_booking", factory.World.Booking())]));
        using var message = Request(HttpMethod.Post, $"/api/v1/chat/sessions/{conversation}/messages?schoolId={school}", factory.Token(), new { text = "Preview", schoolId = school });
        using var reply = await client.SendAsync(message); Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var result = await reply.Content.ReadFromJsonAsync<ChatReply>(); Assert.NotNull(result?.PreviewId);
        using var previewRequest = Request(HttpMethod.Get, $"/api/v1/chat/action-previews/{result!.PreviewId}?schoolId={school}", factory.Token());
        using var previewResponse = await client.SendAsync(previewRequest); var preview = (await previewResponse.Content.ReadFromJsonAsync<PreviewResponse>())!;
        using var approve = Request(HttpMethod.Post, $"/api/v1/chat/action-previews/{preview.PreviewId}/confirmations?schoolId={school}", factory.Token(), new ApprovalRequest(preview.OperationId, preview.ArgumentsFingerprint, true));
        approve.Headers.Remove("Idempotency-Key"); approve.Headers.Add("Idempotency-Key", preview.OperationId.ToString()); approve.Headers.Add("Origin", "https://localhost:4200");
        using var confirmed = await client.SendAsync(approve); Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode); Assert.False((await confirmed.Content.ReadFromJsonAsync<ApprovalResponse>())!.ExecutionAllowed);
        var query = new ConfirmationQuery(school, conversation, preview.PreviewId, factory.World.Actor.IdentityId, preview.OperationId, preview.ArgumentsFingerprint);
        using var internalRequest = Request(HttpMethod.Post, "/internal/v1/chat/confirmations/validate", factory.Token("vairas-chat-internal", "mcp-fixture", scope: "confirmations.validate"), query);
        using var proof = await client.SendAsync(internalRequest); Assert.Equal(HttpStatusCode.OK, proof.StatusCode);
        var evidence = await proof.Content.ReadFromJsonAsync<ConfirmationEvidence>(); Assert.True(evidence!.Confirmed); Assert.False(evidence.ExecutionAllowed);
        using var history = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/chat/sessions/{conversation}/messages?schoolId={school}", factory.Token())); Assert.Equal(HttpStatusCode.OK, history.StatusCode);
    }
    [Theory]
    [InlineData("none")][InlineData("wrong-audience")][InlineData("invalid-subject")][InlineData("invalid-session")][InlineData("workload")]
    public async Task Invalid_entry_credentials_fail_before_owners_or_models(string variant)
    {
        await using var factory = new ChatFactory(); using var client = factory.Client();
        var token = variant switch { "none" => null, "wrong-audience" => factory.Token("vairas-school-management"), "invalid-subject" => factory.Token(subject: "user"), "invalid-session" => factory.Token(session: "bad"), _ => factory.Token("vairas-chat-internal", "mcp-fixture", scope: "confirmations.validate") };
        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/chat/sessions?schoolId={factory.World.School}", token));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); Assert.Equal(0, factory.World.Model.Calls);
    }
    [Theory]
    [InlineData("user")][InlineData("no-scope")][InlineData("unknown-client")]
    public async Task Confirmation_query_requires_workload_audience_scope_and_allowlist(string variant)
    {
        await using var factory = new ChatFactory(); using var client = factory.Client();
        var token = variant == "user" ? factory.Token() : factory.Token("vairas-chat-internal", variant == "unknown-client" ? "other" : "mcp-fixture", scope: variant == "no-scope" ? "other" : "confirmations.validate");
        using var response = await client.SendAsync(Request(HttpMethod.Post, "/internal/v1/chat/confirmations/validate", token, new ConfirmationQuery(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64))));
        Assert.Equal(variant == "user" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
    }
    [Theory]
    [InlineData(null)][InlineData("https://evil.invalid")]
    public async Task Approval_does_not_accept_missing_or_untrusted_origin(string? origin)
    {
        await using var factory = new ChatFactory(); using var client = factory.Client();
        using var request = Request(HttpMethod.Post, $"/api/v1/chat/action-previews/{Guid.NewGuid()}/confirmations?schoolId={factory.World.School}", factory.Token(), new ApprovalRequest(Guid.NewGuid(), new string('a', 64), true));
        if (origin is not null) request.Headers.Add("Origin", origin);
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Empty(factory.World.Store.Rows<Vairas.ChatBackend.Domain.ActionConfirmation>());
    }
    [Theory]
    [InlineData("{\"schoolId\":\"11111111-1111-4111-8111-111111111111\",\"ownerUserId\":\"11111111-1111-4111-8111-111111111111\"}")]
    [InlineData("{\"schoolId\":\"11111111-1111-4111-8111-111111111111\",\"schoolId\":\"22222222-2222-4222-8222-222222222222\"}")]
    public async Task Strict_JSON_rejects_actor_injection_and_duplicate_fields(string body)
    {
        await using var factory = new ChatFactory(); using var client = factory.Client();
        using var request = Request(HttpMethod.Post, "/api/v1/chat/sessions", factory.Token()); request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"); request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
    [Fact]
    public async Task Foreign_identity_cannot_read_another_users_history()
    {
        await using var factory = new ChatFactory(); using var client = factory.Client(); var session = await Session(factory, client);
        using var response = await client.SendAsync(Request(HttpMethod.Get, $"/api/v1/chat/sessions/{session}/messages?schoolId={factory.World.School}", factory.Token(subject: Guid.NewGuid().ToString())));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
    [Fact]
    public async Task Actual_OpenAPI_is_valid_and_can_be_exported_without_claiming_live_model()
    {
        await using var factory = new ChatFactory(); using var client = factory.Client(); using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var json = await response.Content.ReadAsStringAsync();
        var parsed = OpenApiDocument.Parse(json, "json"); Assert.Empty(parsed.Diagnostic!.Errors);
        Assert.Empty(parsed.Document!.Validate(ValidationRuleSet.GetDefaultRuleSet()));
        using var document = JsonDocument.Parse(json); Assert.Equal(14, document.RootElement.GetProperty("paths").EnumerateObject().Sum(p => p.Value.EnumerateObject().Count(o => o.Name is "get" or "post")));
        if (Environment.GetEnvironmentVariable("VAIRAS_CHAT_EXPORT_CONTRACTS") == "on")
        {
            var directory = Path.Combine(ChatPostgres.Root, "api"); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "chat.openapi.json"), json);
            await File.WriteAllTextAsync(Path.Combine(directory, "chat.openapi.yaml"), await parsed.Document!.SerializeAsYamlAsync(OpenApiSpecVersion.OpenApi3_1));
        }
    }
}
