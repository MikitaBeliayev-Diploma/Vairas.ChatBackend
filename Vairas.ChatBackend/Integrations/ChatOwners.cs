using System.Globalization;
using System.Text.Json;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ChatBackend.Configuration.Options;
namespace Vairas.ChatBackend.Integrations;

public sealed class ChatOwners(OwnerTransport transport, DependencyOptions options, TimeProvider clock, IMcpTools tools) : IChatOwners
{
    public async Task<ChatActor> ReadmitAsync(ChatActor actor, CancellationToken ct)
    {
        var start = clock.GetUtcNow();
        var session = await transport.SendAsync(options.AuthBaseUrl, "internal/v1/sessions/validate", HttpMethod.Post, new { identityId = actor.IdentityId, sessionId = actor.SessionId }, null, ct);
        if (session.GetProperty("identityId").GetGuid() != actor.IdentityId || session.GetProperty("sessionId").GetGuid() != actor.SessionId) throw ChatErrors.Dependency("IDENTITY_BINDING_INVALID");
        if (!session.GetProperty("active").GetBoolean()) throw new UnauthorizedException("SESSION_INVALID");
        var until = session.GetProperty("validUntil").GetDateTimeOffset(); ChatRules.Fresh(until, clock.GetUtcNow());
        var profile = await transport.SendAsync(options.BusinessApiBaseUrl, "api/v1/users/me", HttpMethod.Get, null, actor.UserBearer, ct, concealForbidden: false);
        var user = profile.GetProperty("userId").GetGuid();
        if (user == Guid.Empty || actor.UserId != Guid.Empty && actor.UserId != user) throw ChatErrors.Dependency("IDENTITY_BINDING_INVALID");
        if (profile.GetProperty("status").GetString() != "Active") throw new ForbiddenException("PROFILE_INACTIVE");
        return actor with { UserId = user, ValidUntil = ChatRules.Instant(until < start.AddSeconds(30) ? until : start.AddSeconds(30)) };
    }
    public async Task<SchoolAccess> AccessAsync(Guid school, ChatActor actor, CancellationToken ct)
    {
        var result = await transport.SendAsync(options.SchoolBaseUrl, "internal/v1/assistant/access", HttpMethod.Post, new { schoolId = school, actorUserId = actor.UserId }, null, ct);
        return result.Deserialize<SchoolAccess>(Wire.Json) ?? throw ChatErrors.Dependency("OWNER_RESPONSE_INVALID");
    }
    public Task<JsonElement> ReadToolAsync(string name, JsonElement arguments, Guid school, ChatActor actor, CancellationToken ct) => tools.CallAsync(name, arguments, school, actor, ct);
    public async Task<OwnerPreview> PreviewAsync(string name, JsonElement arguments, Guid school, ChatActor actor, CancellationToken ct)
    {
        var result = await tools.CallAsync(name, arguments, school, actor, ct);
        if (result.GetProperty("arguments").GetProperty("schoolId").GetGuid() != school) throw ChatErrors.Dependency("OWNER_PREVIEW_INVALID");
        return result.Deserialize<OwnerPreview>(Wire.Json) ?? throw ChatErrors.Dependency("OWNER_PREVIEW_INVALID");
    }
}
