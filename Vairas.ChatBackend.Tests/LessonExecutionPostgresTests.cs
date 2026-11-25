using Microsoft.EntityFrameworkCore;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ConfirmedActions;
using Xunit;
namespace Vairas.ChatBackend.Tests;

[Trait("Category","Postgres")]
public sealed class LessonExecutionPostgresTests(ChatPostgres pg) : IClassFixture<ChatPostgres>
{
    [Theory][InlineData("Booking")][InlineData("Rescheduling")]
    public async Task Native_guards_allow_exact_dispatch_after_completed_model_run_and_recover_archived_intent(string kind)
    {
        var w=new TestWorld(); w.Settings.Current.ConfirmedLessonActionsEnabled=true;
        var dispatcher=new LessonDispatcherFixture {LoseResponse=true};
        var service=new ChatService(pg.Store,w.Owners,w.Model,w.Settings,w.Clock,dispatcher);
        var session=await service.CreateAsync(new(w.School,LessonActions.Profile),Guid.NewGuid(),w.Actor,default);
        var arguments=kind=="Booking"?w.Booking():Wire.Element(new {lessonId=Guid.NewGuid(),enrollmentId=Guid.NewGuid(),expectedVersion=1,startsAt=w.Clock.Now.AddDays(1),endsAt=w.Clock.Now.AddDays(1).AddMinutes(45),reason="Перенос занятия"});
        w.Model.Turns.Enqueue(new(null,[new("preview",kind=="Booking"?"preview_lesson_booking":"preview_lesson_rescheduling",arguments)]));
        var reply=await service.MessageAsync(w.School,session.SessionId,new("Prepare"),Guid.NewGuid(),w.Actor,default);
        var preview=await service.PreviewAsync(w.School,reply.PreviewId!.Value,w.Actor,default);
        dispatcher.Check=async action=>Assert.True((await service.AuthorizeLessonAsync(new(action),default)).Allowed);
        Assert.Equal("Unknown",(await service.ExecuteAsync(w.School,preview.PreviewId,new(preview.OperationId,preview.ArgumentsFingerprint,true),preview.OperationId,w.Actor,default)).Status);
        Assert.Contains(await pg.Store.PendingExecutionsAsync(w.Clock.Now.AddSeconds(-30),50,default),x=>x.PreviewId==preview.PreviewId);
        await service.ArchiveAsync(w.School,session.SessionId,new(session.Revision),Guid.NewGuid(),w.Actor,default);
        w.Settings.Current.ConfirmedLessonActionsEnabled=false; w.Owners.Active=false;
        await service.ReconcileExecutionsAsync(default);
        await using var db=ChatPostgres.Context(pg.Owner);
        await db.Database.OpenConnectionAsync(); await ((Npgsql.NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
        var stored=await db.Set<ToolInvocation>().SingleAsync(i=>i.Id==preview.PreviewId);
        Assert.Equal(InvocationStatus.Succeeded,stored.Status); Assert.NotNull(stored.RecoveryAfter);
        Assert.Equal(1,dispatcher.Dispatches); Assert.Equal(1,dispatcher.Recoveries);
        Assert.DoesNotContain(await pg.Store.PendingExecutionsAsync(w.Clock.Now,50,default),x=>x.PreviewId==preview.PreviewId);
    }
}
