using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ChatBackend.Persistance;
using Xunit;
namespace Vairas.ChatBackend.Tests;

[Trait("Category", "Postgres")]
public sealed class PostgresTests(ChatPostgres pg) : IClassFixture<ChatPostgres>
{
    [Fact]
    public async Task Native_cursor_and_archive_retry_preserve_exact_results()
    {
        var w = new TestWorld(); var service = pg.Service(w);
        var created = new List<SessionResponse>();
        for (var i = 0; i < 3; i++) created.Add(await service.CreateAsync(new(w.School), Guid.NewGuid(), w.Actor, default));
        var page = await service.ListAsync(w.School, null, 2, w.Actor, default);
        var rest = await service.ListAsync(w.School, page[^1].CreatedAt, 2, w.Actor, default, page[^1].SessionId);
        Assert.Equal(created.Select(s => s.SessionId).Order(), page.Concat(rest).Select(s => s.SessionId).Order());
        var key = Guid.NewGuid(); var session = created[0];
        var archived = await service.ArchiveAsync(w.School, session.SessionId, new(session.Revision), key, w.Actor, default);
        Assert.Equal(archived, await service.ArchiveAsync(w.School, session.SessionId, new(session.Revision), key, w.Actor, default));
        await Business.ThrowsBusinessAsync(() => service.ArchiveAsync(w.School, session.SessionId, new(session.Revision), Guid.NewGuid(), w.Actor, default));
    }
    private async Task<(TestWorld W, ChatService Service, SessionResponse Session)> Start()
    {
        var w = new TestWorld(); var service = pg.Service(w); var session = await service.CreateAsync(new(w.School), Guid.NewGuid(), w.Actor, default); return (w, service, session);
    }
    private async Task<(TestWorld W, ChatService Service, SessionResponse Session, PreviewResponse Preview)> Preview()
    {
        var (w, service, session) = await Start(); w.Model.Turns.Enqueue(new(null, [new("preview", "preview_lesson_booking", w.Booking())]));
        var reply = await service.MessageAsync(w.School, session.SessionId, new("Prepare"), Guid.NewGuid(), w.Actor, default);
        return (w, service, session, await service.PreviewAsync(w.School, reply.PreviewId!.Value, w.Actor, default));
    }
    [Fact]
    public async Task Actual_migrations_grants_six_tables_and_restricted_runtime_are_installed()
    {
        Assert.Equal(6L, await ChatPostgres.Scalar<long>(pg.Owner, "SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name<>'__EFMigrationsHistory'"));
        Assert.False(await ChatPostgres.Scalar<bool>(pg.Runtime, "SELECT has_schema_privilege(current_user,'public','CREATE')"));
        Assert.False(await ChatPostgres.Scalar<bool>(pg.Runtime, "SELECT has_table_privilege(current_user,'ai_model_deployment','UPDATE')"));
        Assert.False(await ChatPostgres.Scalar<bool>(pg.Operator, "SELECT has_table_privilege(current_user,'chat_message','SELECT')"));
    }
    [Fact]
    public async Task Native_completed_run_accepts_one_confirmation_and_returns_its_original_server_time()
    {
        var (w, service, session, preview) = await Preview();
        var approval = await service.ApproveAsync(w.School, preview.PreviewId, new(preview.OperationId, preview.ArgumentsFingerprint, true), preview.OperationId, w.Actor, default);
        var replay = await service.ApproveAsync(w.School, preview.PreviewId, new(preview.OperationId, preview.ArgumentsFingerprint, true), preview.OperationId, w.Actor, default);
        Assert.Equal(approval, replay); Assert.False(approval.ExecutionAllowed);
        var evidence = await service.ConfirmationAsync(new(w.School, session.SessionId, preview.PreviewId, w.Actor.IdentityId, preview.OperationId, preview.ArgumentsFingerprint), default);
        Assert.True(evidence.Confirmed); Assert.False(evidence.ExecutionAllowed);
        Assert.False((await service.ConfirmationAsync(new(w.School, session.SessionId, preview.PreviewId, Guid.NewGuid(), preview.OperationId, preview.ArgumentsFingerprint), default)).Confirmed);
        Assert.Single(await service.AuditAsync(w.School, 50, w.Actor, default));
        await service.MessageAsync(w.School, session.SessionId, new("More text"), Guid.NewGuid(), w.Actor, default);
    }
    [Fact]
    public async Task RLS_blocks_unscoped_foreign_tenant_and_foreign_owner_queries()
    {
        var (w, _, session) = await Start();
        Assert.Equal(0L, await ChatPostgres.Scalar<long>(pg.Runtime, "SELECT count(*) FROM conversation"));
        await using var owner = await pg.Store.OpenAsync(w.School, w.Actor.UserId, false, default);
        Assert.NotNull(await owner.FindAsync<Conversation>(c => c.Id == session.SessionId, default));
        await using var foreign = await pg.Store.OpenAsync(w.School, Guid.NewGuid(), false, default);
        Assert.Null(await foreign.FindAsync<Conversation>(c => c.Id == session.SessionId, default));
        await using var tenant = await pg.Store.OpenAsync(Guid.NewGuid(), w.Actor.UserId, false, default);
        Assert.Null(await tenant.FindAsync<Conversation>(c => c.Id == session.SessionId, default));
    }
    [Fact]
    public async Task Native_concurrent_creation_and_message_retries_do_not_duplicate_rows()
    {
        var w = new TestWorld(); var service = pg.Service(w); var key = Guid.NewGuid();
        var sessions = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.CreateAsync(new(w.School), key, w.Actor, default)));
        Assert.All(sessions, s => Assert.Equal(sessions[0], s));
        var messageKey = Guid.NewGuid(); var reply = await service.MessageAsync(w.School, sessions[0].SessionId, new("One"), messageKey, w.Actor, default);
        Assert.Equal(reply, await service.MessageAsync(w.School, sessions[0].SessionId, new("One"), messageKey, w.Actor, default)); Assert.Equal(1, w.Model.Calls);
        Assert.Equal(2, (await service.HistoryAsync(w.School, sessions[0].SessionId, 0, 50, w.Actor, default)).Count);
    }
    [Fact]
    public async Task Native_confirmation_race_has_one_winner_and_DB_forbids_business_dispatch()
    {
        var (w, service, session, preview) = await Preview(); var request = new ApprovalRequest(preview.OperationId, preview.ArgumentsFingerprint, true);
        var replies = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.ApproveAsync(w.School, preview.PreviewId, request, preview.OperationId, w.Actor, default)));
        Assert.All(replies, r => Assert.Equal(replies[0], r));
        var error = await Assert.ThrowsAsync<PostgresException>(() => ChatPostgres.Sql(pg.Runtime, $"BEGIN; SELECT set_config('vairas.school_id','{w.School}',true),set_config('vairas.user_id','{w.Actor.UserId}',true); UPDATE tool_invocation SET status='Executing',revision=revision+1,started_at=clock_timestamp() WHERE id='{preview.PreviewId}'; COMMIT;"));
        Assert.Contains("business dispatch disabled", error.MessageText);
        Assert.Equal("Confirmed", (await service.PreviewAsync(w.School, preview.PreviewId, w.Actor, default)).Status);
    }
    [Fact]
    public async Task Native_message_and_confirmation_mutation_is_denied_and_membership_change_archives()
    {
        var (w, service, session, preview) = await Preview();
        var error = await Assert.ThrowsAsync<PostgresException>(() => ChatPostgres.Sql(pg.Runtime, "UPDATE chat_message SET content='changed'")); Assert.Equal("42501", error.SqlState);
        w.Owners.Stamp = new string('b', 64);
        await Business.ThrowsBusinessAsync(() => service.GetAsync(w.School, session.SessionId, w.Actor, default));
        Assert.Equal("Archived", await ChatPostgres.Scalar<string>(pg.Owner, $"SELECT status::text FROM conversation WHERE id='{session.SessionId}'"));
        Assert.Equal("Cancelled", await ChatPostgres.Scalar<string>(pg.Owner, $"SELECT status::text FROM tool_invocation WHERE id='{preview.PreviewId}'"));
    }
    [Fact]
    public async Task Native_timeout_recovery_does_not_accept_a_late_model_completion()
    {
        var (w, service, session) = await Start(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        w.Model.BeforeResponse = async _ => { entered.TrySetResult(); await release.Task; };
        var first = service.MessageAsync(w.School, session.SessionId, new("Wait"), Guid.NewGuid(), w.Actor, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var run = await ChatPostgres.Scalar<Guid>(pg.Owner, $"SELECT id FROM assistant_run WHERE conversation_id='{session.SessionId}'");
        w.Clock.Now = w.Clock.Now.AddSeconds(61); Assert.Equal("Failed", (await service.RunAsync(w.School, session.SessionId, run, w.Actor, default)).Status);
        release.TrySetResult(); await Business.ThrowsBusinessAsync(() => first);
        Assert.Single(await service.HistoryAsync(w.School, session.SessionId, 0, 50, w.Actor, default));
    }
}
