using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;

namespace Vairas.ChatBackend.Tests;
public sealed class TestWorld
{
    public TestClock Clock { get; } = new();
    public MemoryStore Store { get; } = new();
    public TestOwners Owners { get; }
    public TestModel Model { get; } = new();
    public TestSettings Settings { get; } = new();
    public Guid School { get; } = Guid.NewGuid();
    public ChatActor Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic-bearer-not-for-persistence", DateTimeOffset.MinValue);
    public ChatService Service { get; }
    public TestWorld() { Owners = new(Clock); Owners.Bindings[Actor.IdentityId] = Actor.UserId; Service = new(Store, Owners, Model, Settings, Clock); }
    public Task<SessionResponse> Session() => Service.CreateAsync(new(School), Guid.NewGuid(), Actor, default);
    public JsonElement Booking() => Wire.Element(new { schoolId = School, branchId = Guid.NewGuid(), enrollmentId = Guid.NewGuid(), studentId = Guid.NewGuid(), teacherId = Guid.NewGuid(), vehicleId = Guid.NewGuid(), category = "B", startsAt = Clock.Now.AddDays(1), endsAt = Clock.Now.AddDays(1).AddMinutes(45) });
    public async Task<(SessionResponse Session, PreviewResponse Preview)> Preview()
    {
        var session = await Session(); Model.Turns.Enqueue(new(null, [new("call", "preview_lesson_booking", Booking())]));
        var reply = await Service.MessageAsync(School, session.SessionId, new("Prepare a synthetic booking"), Guid.NewGuid(), Actor, default);
        return (session, await Service.PreviewAsync(School, reply.PreviewId!.Value, Actor, default));
    }
}
