using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;

namespace Vairas.ChatBackend.Tests;

public sealed class TestOwners(TestClock clock) : IChatOwners
{
    public Dictionary<Guid, Guid> Bindings { get; } = [];
    public bool Active { get; set; } = true;
    public bool Allowed { get; set; } = true;
    public bool CanAudit { get; set; } = true;
    public string Stamp { get; set; } = new('a', 64);
    public int Revision { get; set; } = 1;
    public bool Fail { get; set; }
    public int Reads { get; private set; }
    public Task<ChatActor> ReadmitAsync(ChatActor actor, CancellationToken ct)
    { if (Fail) throw ChatErrors.Dependency(); if (!Active) throw new UnauthorizedException("SESSION_INVALID"); return Task.FromResult(actor with { UserId = actor.UserId == Guid.Empty ? Bindings.GetValueOrDefault(actor.IdentityId, actor.IdentityId) : actor.UserId, ValidUntil = clock.GetUtcNow().AddSeconds(30) }); }
    public Task<SchoolAccess> AccessAsync(Guid school, ChatActor actor, CancellationToken ct) => Task.FromResult(new SchoolAccess(school, actor.UserId, Allowed, CanAudit, Stamp, clock.GetUtcNow().AddSeconds(30)));
    public Task<JsonElement> ReadToolAsync(string name, JsonElement arguments, Guid school, ChatActor actor, CancellationToken ct)
    { Reads++; return Task.FromResult(Wire.Element(new { source = "owner", schoolId = school, confirmedMinutes = (int?)null, state = "Unverified" })); }
    public Task<OwnerPreview> PreviewAsync(string name, JsonElement arguments, Guid school, ChatActor actor, CancellationToken ct)
    {
        var args = JsonNode.Parse(arguments.GetRawText())!.AsObject(); args["schoolId"] = school;
        return Task.FromResult(new OwnerPreview("scheduling-preview/1", name == "preview_lesson_booking" ? "Booking" : "Rescheduling", Wire.Element(args), Wire.Element(new { revision = Revision }), Wire.Element(new { schoolId = school, notice = "Synthetic owner preview" })));
    }
}
