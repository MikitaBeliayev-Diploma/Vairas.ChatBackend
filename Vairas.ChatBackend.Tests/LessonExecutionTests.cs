using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ConfirmedActions;
using Xunit;
namespace Vairas.ChatBackend.Tests;
[Trait("Category", "Unit")]
public sealed class LessonExecutionTests
{
    private static async Task<(TestWorld World, ChatService Service, LessonDispatcherFixture Dispatcher, PreviewResponse Preview)> Prepare(string kind = "Booking")
    {
        var w = new TestWorld(); w.Settings.Current.ConfirmedLessonActionsEnabled = true;
        var dispatcher = new LessonDispatcherFixture(); var service = new ChatService(w.Store, w.Owners, w.Model, w.Settings, w.Clock, dispatcher);
        var session = await service.CreateAsync(new(w.School, LessonActions.Profile), Guid.NewGuid(), w.Actor, default);
        var args = kind == "Booking" ? w.Booking() : Wire.Element(new { lessonId = Guid.NewGuid(), enrollmentId = Guid.NewGuid(), expectedVersion = 1, startsAt = w.Clock.Now.AddDays(1), endsAt = w.Clock.Now.AddDays(1).AddMinutes(45), reason = "Изменение времени" });
        w.Model.Turns.Enqueue(new(null, [new("preview", kind == "Booking" ? "preview_lesson_booking" : "preview_lesson_rescheduling", args)]));
        var reply = await service.MessageAsync(w.School, session.SessionId, new("Prepare exact action"), Guid.NewGuid(), w.Actor, default);
        var preview = await service.PreviewAsync(w.School, reply.PreviewId!.Value, w.Actor, default);
        dispatcher.Check = async action => Assert.True((await service.AuthorizeLessonAsync(new(action), default)).Allowed);
        return (w, service, dispatcher, preview);
    }
    [Theory][InlineData("Booking")][InlineData("Rescheduling")]
    public async Task Exact_human_action_dispatches_once_and_replays_owner_receipt(string kind)
    {
        var (w,s,d,p) = await Prepare(kind); var approval = new ApprovalRequest(p.OperationId,p.ArgumentsFingerprint,true);
        var results = await Task.WhenAll(Enumerable.Range(0,4).Select(_ => s.ExecuteAsync(w.School,p.PreviewId,approval,p.OperationId,w.Actor,default)));
        var receipt = await s.ExecutionAsync(w.School,p.PreviewId,w.Actor,default);
        Assert.Equal("Committed",receipt.Status); Assert.Equal(1,d.Dispatches); Assert.All(results,r => Assert.Equal(p.OperationId,r.OperationId));
        Assert.Single(w.Store.Rows<ActionConfirmation>()); Assert.Equal(InvocationStatus.Succeeded,w.Store.Rows<ToolInvocation>().Single().Status);
        Assert.Equal(receipt, await s.ExecuteAsync(w.School,p.PreviewId,approval,p.OperationId,w.Actor,default));
        Assert.Equal(2,w.Model.Calls); Assert.All(w.Model.Prompts,prompt => Assert.DoesNotContain(prompt,m => m.Calls?.Any(c => c.Name.StartsWith("create_")) == true));
    }
    [Theory][InlineData("digest")][InlineData("operation")][InlineData("session")][InlineData("stale")][InlineData("expired")][InlineData("disabled")][InlineData("denied")]
    public async Task Invalid_consent_never_dispatches(string variant)
    {
        var (w,s,d,p) = await Prepare(); var actor=w.Actor; var approval=new ApprovalRequest(p.OperationId,p.ArgumentsFingerprint,true);
        switch(variant)
        {
            case "digest": approval=approval with {ArgumentsFingerprint=new('b',64)}; break;
            case "operation": approval=approval with {OperationId=Guid.NewGuid()}; break;
            case "session": actor=actor with {SessionId=Guid.NewGuid()}; break;
            case "stale": w.Owners.Revision++; break;
            case "expired": w.Clock.Now=w.Clock.Now.AddMinutes(6); break;
            case "disabled": w.Settings.Current.ConfirmedLessonActionsEnabled=false; break;
            case "denied": w.Owners.Allowed=false; break;
        }
        await Business.ThrowsBusinessAsync(() => s.ExecuteAsync(w.School,p.PreviewId,approval,p.OperationId,actor,default)); Assert.Equal(0,d.Dispatches);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task Lost_response_or_failed_send_is_unknown_until_owner_recovery_even_after_archive(bool committed)
    {
        var (w,s,d,p)=await Prepare(); d.LoseResponse=committed; d.ThrowBeforeCommit=!committed;
        var approval=new ApprovalRequest(p.OperationId,p.ArgumentsFingerprint,true);
        Assert.Equal("Unknown",(await s.ExecuteAsync(w.School,p.PreviewId,approval,p.OperationId,w.Actor,default)).Status);
        var conversation=w.Store.Rows<Conversation>().Single();
        await s.ArchiveAsync(w.School,conversation.Id,new(conversation.Revision),Guid.NewGuid(),w.Actor,default);
        Assert.Equal(InvocationStatus.Unknown,w.Store.Rows<ToolInvocation>().Single().Status);
        w.Owners.Active=false; w.Settings.Current.ConfirmedLessonActionsEnabled=false;
        await s.ReconcileExecutionsAsync(default);
        Assert.Equal(committed?InvocationStatus.Succeeded:InvocationStatus.Rejected,w.Store.Rows<ToolInvocation>().Single().Status);
        Assert.Equal(1,d.Dispatches); Assert.Equal(1,d.Recoveries);
        await Business.ThrowsBusinessAsync(()=>s.ExecutionAsync(w.School,p.PreviewId,w.Actor,default));
    }
    [Fact]
    public async Task Record_only_profile_cannot_be_upgraded_by_confirmation_or_execution()
    {
        var w=new TestWorld(); var (_,p)=await w.Preview();
        await w.Service.ApproveAsync(w.School,p.PreviewId,new(p.OperationId,p.ArgumentsFingerprint,true),p.OperationId,w.Actor,default);
        var error=await Business.ThrowsBusinessAsync(()=>w.Service.ExecuteAsync(w.School,p.PreviewId,new(p.OperationId,p.ArgumentsFingerprint,true),p.OperationId,w.Actor,default));
        Assert.Equal("PREVIEW_ONLY",error.Code);
    }
    [Fact]
    public async Task Authorization_is_bound_to_the_actual_payload_and_expires_only_for_new_execution()
    {
        var (w,s,d,p)=await Prepare(); d.LoseResponse=true;
        await s.ExecuteAsync(w.School,p.PreviewId,new(p.OperationId,p.ArgumentsFingerprint,true),p.OperationId,w.Actor,default);
        var action=d.Action!;
        await Assert.ThrowsAsync<InvalidOperationException>(()=>s.AuthorizeLessonAsync(new(action with {Arguments=Wire.Element(new {schoolId=w.School})}),default));
        Assert.False((await s.AuthorizeLessonAsync(new(action with {Binding=action.Binding with {UserId=Guid.NewGuid()}}),default)).Allowed);
        w.Clock.Now=w.Clock.Now.AddMinutes(6);
        Assert.False((await s.AuthorizeLessonAsync(new(action),default)).Allowed);
        Assert.True((await s.AuthorizeLessonAsync(new(action,true),default)).Allowed);
    }
}
