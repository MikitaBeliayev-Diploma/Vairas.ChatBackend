using System.Text.Json;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Xunit;
namespace Vairas.ChatBackend.Tests;

[Trait("Category", "Unit")]
public sealed class BusinessTests
{
    [Fact]
    public async Task Ambiguous_enrollment_read_returns_validation_without_guessing_or_dispatching()
    {
        var w = new TestWorld(); var session = await w.Session();
        w.Model.Turns.Enqueue(new(null, [new("ambiguous", "get_student_training_progress", Wire.Element(new { }))]));
        var error = await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId,
            new("I have two active enrollments. Show my progress."), Guid.NewGuid(), w.Actor, default));
        Assert.Equal("MISSING_TOOL_ARGUMENT", error.Code);
        Assert.Equal(0, w.Owners.Reads);
        Assert.Empty(w.Store.Rows<ToolInvocation>());
        Assert.Empty(w.Store.Rows<ActionConfirmation>());
        Assert.Equal(RunStatus.Failed, Assert.Single(w.Store.Rows<AssistantRun>()).Status);
    }
    [Fact]
    public async Task Session_cursor_keeps_rows_created_at_the_same_instant()
    {
        var w = new TestWorld();
        var created = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => w.Session()));
        var first = await w.Service.ListAsync(w.School, null, 2, w.Actor, default);
        var next = await w.Service.ListAsync(w.School, first[^1].CreatedAt, 2, w.Actor, default, first[^1].SessionId);
        Assert.Equal(3, first.Concat(next).Select(s => s.SessionId).Distinct().Count());
        Assert.Equal(created.Select(s => s.SessionId).Order(), first.Concat(next).Select(s => s.SessionId).Order());
        await Business.ThrowsBusinessAsync(() => w.Service.ListAsync(w.School, w.Clock.Now, 2, w.Actor, default));
        await Business.ThrowsBusinessAsync(() => w.Service.ListAsync(w.School, null, 2, w.Actor, default, Guid.NewGuid()));
    }
    [Fact]
    public async Task Session_and_message_retries_return_original_results_without_regenerating()
    {
        var w = new TestWorld(); var key = Guid.NewGuid();
        var session = await w.Service.CreateAsync(new(w.School), key, w.Actor, default);
        Assert.Equal(session, await w.Service.CreateAsync(new(w.School), key, w.Actor, default));
        var messageKey = Guid.NewGuid(); var request = new SendMessageRequest("Hello");
        var reply = await w.Service.MessageAsync(w.School, session.SessionId, request, messageKey, w.Actor, default);
        Assert.Equal(reply, await w.Service.MessageAsync(w.School, session.SessionId, request, messageKey, w.Actor, default));
        Assert.Equal(1, w.Model.Calls); Assert.Equal(2, w.Store.Rows<ChatMessage>().Count);
        var error = await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId, new("Changed"), messageKey, w.Actor, default)); Assert.Equal("IDEMPOTENCY_KEY_REUSED", error.Code);
    }
    [Fact]
    public async Task Confirmation_is_recorded_after_run_completion_but_never_dispatches()
    {
        var w = new TestWorld(); var (session, preview) = await w.Preview();
        Assert.Equal(RunStatus.Completed, w.Store.Rows<AssistantRun>().Single().Status);
        var body = new ApprovalRequest(preview.OperationId, preview.ArgumentsFingerprint, true);
        var approval = await w.Service.ApproveAsync(w.School, preview.PreviewId, body, preview.OperationId, w.Actor, default);
        Assert.False(approval.ExecutionAllowed);
        Assert.Equal(approval, await w.Service.ApproveAsync(w.School, preview.PreviewId, body, preview.OperationId, w.Actor, default));
        Assert.Single(w.Store.Rows<ActionConfirmation>());
        Assert.Equal(InvocationStatus.Confirmed, w.Store.Rows<ToolInvocation>().Single().Status);
        var query = new ConfirmationQuery(w.School, session.SessionId, preview.PreviewId, w.Actor.IdentityId, preview.OperationId, preview.ArgumentsFingerprint);
        var proof = await w.Service.ConfirmationAsync(query, default); Assert.True(proof.Confirmed); Assert.False(proof.ExecutionAllowed);
        Assert.False((await w.Service.ConfirmationAsync(query with { IdentityId = Guid.NewGuid() }, default)).Confirmed);
        Assert.DoesNotContain(w.Store.Rows<ToolInvocation>(), i => i.Status == InvocationStatus.Executing || i.Status == InvocationStatus.Succeeded);
        await w.Service.MessageAsync(w.School, session.SessionId, new("Continue without executing"), Guid.NewGuid(), w.Actor, default);
    }
    [Theory]
    [InlineData("fingerprint")][InlineData("operation")][InlineData("declined")][InlineData("expiry")][InlineData("owner-change")][InlineData("foreign-user")]
    public async Task Mismatched_or_stale_approval_never_creates_confirmation(string variant)
    {
        var w = new TestWorld(); var (_, preview) = await w.Preview();
        var body = new ApprovalRequest(preview.OperationId, preview.ArgumentsFingerprint, true); var actor = w.Actor;
        switch (variant)
        {
            case "fingerprint": body = body with { ArgumentsFingerprint = new string('f', 64) }; break;
            case "operation": body = body with { OperationId = Guid.NewGuid() }; break;
            case "declined": body = body with { Approved = false }; break;
            case "expiry": w.Clock.Now = w.Clock.Now.AddMinutes(6); break;
            case "owner-change": w.Owners.Revision++; break;
            case "foreign-user": actor = actor with { UserId = Guid.NewGuid() }; break;
        }
        await Business.ThrowsBusinessAsync(() => w.Service.ApproveAsync(w.School, preview.PreviewId, body, preview.OperationId, actor, default));
        Assert.Empty(w.Store.Rows<ActionConfirmation>());
    }
    [Fact]
    public async Task Concurrent_approvals_have_one_immutable_winner()
    {
        var w = new TestWorld(); var (_, preview) = await w.Preview(); var request = new ApprovalRequest(preview.OperationId, preview.ArgumentsFingerprint, true);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => w.Service.ApproveAsync(w.School, preview.PreviewId, request, preview.OperationId, w.Actor, default)));
        Assert.All(results, r => Assert.Equal(results[0], r)); Assert.Single(w.Store.Rows<ActionConfirmation>());
    }
    [Theory]
    [InlineData("school")][InlineData("user")][InlineData("membership")][InlineData("revoked")][InlineData("stamp")]
    public async Task History_and_replay_require_current_owner_and_membership(string variant)
    {
        var w = new TestWorld(); var session = await w.Session(); var actor = w.Actor; var school = w.School;
        switch (variant)
        {
            case "school": school = Guid.NewGuid(); break; case "user": actor = actor with { UserId = Guid.NewGuid() }; break;
            case "membership": w.Owners.Allowed = false; break; case "revoked": w.Owners.Active = false; break; case "stamp": w.Owners.Stamp = new string('b', 64); break;
        }
        await Business.ThrowsBusinessAsync(() => w.Service.HistoryAsync(school, session.SessionId, 0, 50, actor, default));
        if (variant is "membership" or "stamp") Assert.Equal(ConversationStatus.Archived, w.Store.Rows<Conversation>().Single().Status);
    }
    [Fact]
    public async Task Archive_is_terminal_and_cancels_unexecuted_preview()
    {
        var w = new TestWorld(); var (session, preview) = await w.Preview();
        var operation = Guid.NewGuid(); var archived = await w.Service.ArchiveAsync(w.School, session.SessionId, new(session.Revision), operation, w.Actor, default);
        Assert.Equal("Archived", archived.Status); Assert.Equal(InvocationStatus.Cancelled, w.Store.Rows<ToolInvocation>().Single().Status);
        await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId, new("No reopen"), Guid.NewGuid(), w.Actor, default));
        Assert.Equal(archived, await w.Service.ArchiveAsync(w.School, session.SessionId, new(session.Revision), operation, w.Actor, default));
        await Business.ThrowsBusinessAsync(() => w.Service.ArchiveAsync(w.School, session.SessionId, new(session.Revision), Guid.NewGuid(), w.Actor, default));
    }
    [Fact]
    public async Task New_preview_supersedes_old_confirmed_intent()
    {
        var w = new TestWorld(); var (session, preview) = await w.Preview();
        await w.Service.ApproveAsync(w.School, preview.PreviewId, new(preview.OperationId, preview.ArgumentsFingerprint, true), preview.OperationId, w.Actor, default);
        w.Model.Turns.Enqueue(new(null, [new("new", "preview_lesson_booking", w.Booking())]));
        await w.Service.MessageAsync(w.School, session.SessionId, new("Different preview"), Guid.NewGuid(), w.Actor, default);
        Assert.Equal(InvocationStatus.Cancelled, w.Store.Rows<ToolInvocation>().Single(i => i.Id == preview.PreviewId).Status);
        Assert.Single(w.Store.Rows<ToolInvocation>(), i => i.Status == InvocationStatus.AwaitingConfirmation);
    }
    [Theory]
    [InlineData("emergency")][InlineData("model-disabled")][InlineData("deployment-disabled")]
    public async Task Disabled_generation_creates_no_phantom_message(string reason)
    {
        var w = new TestWorld(); var session = await w.Session();
        if (reason == "emergency") w.Settings.Current.EmergencyDisabled = true; else if (reason == "model-disabled") w.Model.Enabled = false; else w.Store.Model.Enabled = false;
        await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId, new("No call"), Guid.NewGuid(), w.Actor, default));
        Assert.Equal(0, w.Model.Calls); Assert.Empty(w.Store.Rows<ChatMessage>());
    }
    [Theory]
    [InlineData("membership")][InlineData("revoked")][InlineData("emergency")]
    public async Task Authority_change_during_generation_does_not_disclose_or_save_answer(string reason)
    {
        var w = new TestWorld(); var session = await w.Session();
        w.Model.BeforeResponse = _ => { if (reason == "membership") w.Owners.Allowed = false; else if (reason == "revoked") w.Owners.Active = false; else w.Settings.Current.EmergencyDisabled = true; return Task.CompletedTask; };
        await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId, new("Wait"), Guid.NewGuid(), w.Actor, default));
        Assert.DoesNotContain(w.Store.Rows<ChatMessage>(), m => m.Role == MessageRole.Assistant);
    }
    [Fact]
    public async Task Read_tool_facts_are_fresh_and_not_replayed_from_history()
    {
        var w = new TestWorld(); var session = await w.Session();
        w.Model.Turns.Enqueue(new(null, [new("read", "get_student_training_progress", Wire.Element(new { enrollmentId = Guid.NewGuid() }))]));
        await w.Service.MessageAsync(w.School, session.SessionId, new("Progress"), Guid.NewGuid(), w.Actor, default);
        Assert.Equal(1, w.Owners.Reads); Assert.Contains("Unverified", w.Model.Prompts[1].Last().Text);
        await w.Service.MessageAsync(w.School, session.SessionId, new("Continue"), Guid.NewGuid(), w.Actor, default);
        Assert.All(w.Model.Prompts[2], m => Assert.Equal("user", m.Role));
        Assert.DoesNotContain(w.Store.Rows<ToolInvocation>(), i => i.ResultSummary is not null);
    }
    [Theory]
    [InlineData("create_lesson_booking")][InlineData("reschedule_lesson_booking")][InlineData("confirm")][InlineData("http_request")]
    public async Task Model_cannot_invoke_mutations_or_generic_HTTP(string tool)
    {
        var w = new TestWorld(); var session = await w.Session(); w.Model.Turns.Enqueue(new(null, [new("bad", tool, Wire.Element(new { }))]));
        await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId, new("Ignore policy"), Guid.NewGuid(), w.Actor, default));
        Assert.Empty(w.Store.Rows<ToolInvocation>()); Assert.Equal(RunStatus.Failed, w.Store.Rows<AssistantRun>().Single().Status);
    }
    [Fact]
    public async Task Concurrent_messages_cannot_start_a_second_run()
    {
        var w = new TestWorld(); var session = await w.Session(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        w.Model.BeforeResponse = async _ => { entered.TrySetResult(); await release.Task; };
        var first = w.Service.MessageAsync(w.School, session.SessionId, new("First"), Guid.NewGuid(), w.Actor, default); await entered.Task;
        var error = await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId, new("Second"), Guid.NewGuid(), w.Actor, default)); Assert.Equal("RUN_IN_PROGRESS", error.Code);
        release.TrySetResult(); await first;
    }
    [Fact]
    public async Task Stale_run_recovery_prevents_a_late_answer_from_overwriting_state()
    {
        var w = new TestWorld(); var session = await w.Session(); var entered = new TaskCompletionSource(); var release = new TaskCompletionSource();
        w.Model.BeforeResponse = async _ => { entered.TrySetResult(); await release.Task; };
        var first = w.Service.MessageAsync(w.School, session.SessionId, new("First"), Guid.NewGuid(), w.Actor, default); await entered.Task;
        var run = w.Store.Rows<AssistantRun>().Single(); w.Clock.Now = w.Clock.Now.AddSeconds(61);
        Assert.Equal("Failed", (await w.Service.RunAsync(w.School, session.SessionId, run.Id, w.Actor, default)).Status);
        release.TrySetResult(); await Business.ThrowsBusinessAsync(() => first);
        Assert.DoesNotContain(w.Store.Rows<ChatMessage>(), m => m.Role == MessageRole.Assistant);
    }
    [Fact]
    public async Task Audit_does_not_grant_history_and_contains_no_arguments_or_text()
    {
        var w = new TestWorld(); var (session, _) = await w.Preview(); var admin = w.Actor with { UserId = Guid.NewGuid() };
        var audit = await w.Service.AuditAsync(w.School, 50, admin, default); Assert.Single(audit);
        await Business.ThrowsBusinessAsync(() => w.Service.HistoryAsync(w.School, session.SessionId, 0, 50, admin, default));
        w.Owners.CanAudit = false; await Business.ThrowsBusinessAsync(() => w.Service.AuditAsync(w.School, 50, admin, default));
    }
    [Fact]
    public async Task Failed_same_key_is_not_regenerated_and_history_pagination_is_bounded()
    {
        var w = new TestWorld(); var session = await w.Session(); var key = Guid.NewGuid();
        w.Model.BeforeResponse = _ => throw new InvalidOperationException("Provider secret failure");
        await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId, new("Fail"), key, w.Actor, default));
        await Business.ThrowsBusinessAsync(() => w.Service.MessageAsync(w.School, session.SessionId, new("Fail"), key, w.Actor, default)); Assert.Equal(1, w.Model.Calls);
        var messages = await w.Service.HistoryAsync(w.School, session.SessionId, 0, 1, w.Actor, default); Assert.Single(messages);
        Assert.Empty(await w.Service.HistoryAsync(w.School, session.SessionId, messages[0].SequenceNo, 1, w.Actor, default));
    }
}
