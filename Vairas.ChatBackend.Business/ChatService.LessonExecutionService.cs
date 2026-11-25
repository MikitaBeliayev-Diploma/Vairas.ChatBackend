using System.Text.Json;
using Vairas.ChatBackend.Domain;
using Vairas.ConfirmedActions;
namespace Vairas.ChatBackend.Business;

public sealed partial class ChatService
{
    private static LessonAction FrozenAction(ToolInvocation tool)
    {
        var saved = Wire.Read<JsonElement>(tool.Preview!);
        if (!saved.TryGetProperty("executionProfile", out var profile) || profile.GetString() != LessonActions.Profile) throw ChatErrors.Conflict("PREVIEW_ONLY");
        var binding = new LessonActionBinding(tool.SchoolId, tool.ConversationId, tool.Id, tool.OperationId,
            saved.GetProperty("actorIdentityId").GetGuid(), saved.GetProperty("actorUserId").GetGuid(), saved.GetProperty("actorSessionId").GetGuid(),
            saved.GetProperty("kind").GetString()!, tool.ActionDigest!, saved.GetProperty("commandDigest").GetString()!);
        var action = new LessonAction(binding, saved.GetProperty("arguments"), saved.GetProperty("sourceRevisions"));
        LessonActions.Validate(action); return action;
    }
    private static LessonActionReceipt ExecutionDto(ToolInvocation tool) => tool.ResultSummary is not null
        ? JsonSerializer.Deserialize<LessonActionReceipt>(tool.ResultSummary, LessonActions.Json)!
        : new(tool.OperationId, tool.Status.ToString(), Code: tool.ErrorCode);

    public async Task<LessonActionReceipt> ExecutionAsync(Guid school, Guid preview, ChatActor actor, CancellationToken ct)
    {
        (actor, var access) = await Admission(school, actor, ct);
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        var tool = await Preview(session, preview, actor, access, ct); _ = FrozenAction(tool);
        Fresh(actor, access); await session.CommitAsync(ct); return ExecutionDto(tool);
    }
    public async Task<LessonActionReceipt> ExecuteAsync(Guid school, Guid preview, ApprovalRequest request, Guid operation, ChatActor actor, CancellationToken ct)
    {
        if (!request.Approved || request.OperationId != operation || operation == Guid.Empty) throw ChatErrors.Invalid("EXACT_APPROVAL_REQUIRED");
        (actor, var access) = await Admission(school, actor, ct);
        ToolInvocation snapshot;
        await using (var read = await store.OpenAsync(school, actor.UserId, true, ct))
        {
            snapshot = await Preview(read, preview, actor, access, ct); var bound = FrozenAction(snapshot).Binding;
            if (bound.IdentityId != actor.IdentityId || bound.SessionId != actor.SessionId || bound.UserId != actor.UserId || snapshot.OperationId != operation || snapshot.ActionDigest != request.ArgumentsFingerprint) throw ChatErrors.Conflict("CONFIRMATION_MISMATCH");
            await read.CommitAsync(ct);
            if (snapshot.Status is InvocationStatus.Expired or InvocationStatus.Cancelled or InvocationStatus.Failed) throw ChatErrors.Conflict("PREVIEW_NOT_EXECUTABLE");
            if (snapshot.Status is not (InvocationStatus.AwaitingConfirmation or InvocationStatus.Confirmed)) return ExecutionDto(snapshot);
        }
        if (!Options.ConfirmedLessonActionsEnabled || actions is null) throw ChatErrors.Dependency("LESSON_ACTIONS_DISABLED");
        var current = await owners.PreviewAsync(snapshot.ToolName, Wire.Read<JsonElement>(snapshot.CanonicalArguments), school, actor, ct);
        var saved = Wire.Read<JsonElement>(snapshot.Preview!);
        if (current.PreviewVersion != saved.GetProperty("ownerPreviewVersion").GetString() || ChatRules.Hash(current.Arguments) != ChatRules.Hash(saved.GetProperty("arguments")) || ChatRules.Hash(current.SourceRevisions) != ChatRules.Hash(saved.GetProperty("sourceRevisions")) || ChatRules.Hash(current.Display) != ChatRules.Hash(saved.GetProperty("displayedPreview"))) throw ChatErrors.Conflict("PREVIEW_STALE");
        (actor, access) = await Admission(school, actor, ct);
        LessonAction action;
        await using (var session = await store.OpenAsync(school, actor.UserId, true, ct))
        {
            var tool = await Preview(session, preview, actor, access, ct);
            var conversation = await session.FindAsync<Conversation>(c => c.Id == tool.ConversationId, ct);
            if (conversation?.Status != ConversationStatus.Open) throw ChatErrors.Conflict("CONVERSATION_ARCHIVED");
            if (tool.Status is InvocationStatus.Expired or InvocationStatus.Cancelled or InvocationStatus.Failed) throw ChatErrors.Conflict("PREVIEW_NOT_EXECUTABLE");
            if (tool.Status is not (InvocationStatus.AwaitingConfirmation or InvocationStatus.Confirmed)) return ExecutionDto(tool);
            if (tool.Revision != snapshot.Revision || tool.ActionDigest != request.ArgumentsFingerprint || !Options.ConfirmedLessonActionsEnabled) throw ChatErrors.Conflict("PREVIEW_NOT_CONFIRMABLE");
            if (await session.FindAsync<ToolInvocation>(i => i.ConversationId == tool.ConversationId && i.Id != tool.Id && i.Effect == ToolEffect.Write && (i.Status == InvocationStatus.Executing || i.Status == InvocationStatus.Unknown), ct) is not null) throw ChatErrors.Conflict("EXECUTION_IN_PROGRESS");
            if (await session.FindAsync<ActionConfirmation>(c => c.InvocationId == preview, ct) is null)
            {
                session.Add(new ActionConfirmation { SchoolId = school, ConversationId = tool.ConversationId, InvocationId = preview, ConfirmedByUserId = actor.UserId, ConfirmedDigest = tool.ActionDigest!, ConfirmedAt = Now });
                await session.FlushAsync(ct); tool.Status = InvocationStatus.Confirmed; tool.Revision++; await session.FlushAsync(ct);
            }
            tool.Status = InvocationStatus.Executing; tool.StartedAt = Now; tool.Revision++; action = FrozenAction(tool);
            Fresh(actor, access); await session.CommitAsync(ct);
        }
        LessonActionReceipt receipt;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            receipt = await actions.ExecuteAsync(action, actor, deadline.Token);
        }
        catch (Exception) { receipt = new(operation, "Unknown", Code: "EXECUTION_OUTCOME_UNKNOWN"); }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var savedReceipt = await SaveExecutionResult(action, receipt, cleanup.Token);
        (actor, access) = await Admission(school, actor, ct);
        await using var disclosure = await store.OpenAsync(school, actor.UserId, true, ct);
        await Conversation(disclosure, action.Binding.ConversationId, actor, access, ct); Fresh(actor, access);
        return savedReceipt;
    }
    private async Task<LessonActionReceipt> SaveExecutionResult(LessonAction action, LessonActionReceipt receipt, CancellationToken ct)
    {
        var b = action.Binding;
        if (receipt.OperationId != b.OperationId || receipt.Status is not ("Committed" or "Rejected") || receipt.Status == "Committed" && receipt.HttpStatus != (b.Kind == "Booking" ? 201 : 200) || receipt.Status == "Rejected" && receipt.HttpStatus is not (>=400 and <500) || receipt.Status == "Committed" && (receipt.LessonId is null || receipt.LessonId == Guid.Empty || receipt.Version is null or < 1)) receipt = new(b.OperationId, "Unknown", Code: "EXECUTION_OUTCOME_UNKNOWN");
        await using var session = await store.OpenAsync(b.SchoolId, b.UserId, true, ct);
        var tool = await session.FindAsync<ToolInvocation>(i => i.Id == b.PreviewId, ct) ?? throw ChatErrors.Missing();
        if (tool.Status is not (InvocationStatus.Executing or InvocationStatus.Unknown)) return ExecutionDto(tool);
        if (tool.Status == InvocationStatus.Unknown && receipt.Status == "Unknown") return ExecutionDto(tool);
        tool.Status = receipt.Status switch { "Committed" => InvocationStatus.Succeeded, "Rejected" => InvocationStatus.Rejected, _ => InvocationStatus.Unknown };
        tool.ErrorCode = receipt.Code; tool.ResultReference = receipt.LessonId?.ToString("D"); tool.ResultSummary = JsonSerializer.Serialize(receipt, LessonActions.Json); tool.FinishedAt = receipt.Status == "Unknown" ? null : Now; tool.Revision++;
        await session.CommitAsync(ct); return receipt;
    }
    public async Task<LessonAuthorization> AuthorizeLessonAsync(LessonAuthorizationQuery query, CancellationToken ct)
    {
        LessonActions.Validate(query.Action); var b = query.Action.Binding;
        await using var session = await store.OpenAsync(b.SchoolId, b.UserId, false, ct);
        var tool = await session.FindAsync<ToolInvocation>(i => i.Id == b.PreviewId && i.ConversationId == b.ConversationId, ct);
        if (tool is null || tool.Status is not (InvocationStatus.Executing or InvocationStatus.Unknown) || FrozenAction(tool).Binding != b) return new(false);
        var confirmation = await session.FindAsync<ActionConfirmation>(a => a.InvocationId == b.PreviewId && a.ConfirmedDigest == b.ActionDigest && a.ConfirmedByUserId == b.UserId, ct);
        var conversation = await session.FindAsync<Conversation>(c => c.Id == b.ConversationId, ct);
        if (confirmation is null || confirmation.ConfirmedAt >= tool.ExpiresAt || conversation?.ExecutionProfile != LessonActions.Profile) return new(false);
        // Recovery may settle an existing command after revocation/expiry, but can never create one.
        if (!query.Reconcile && (!Options.ConfirmedLessonActionsEnabled || conversation.Status != ConversationStatus.Open || tool.ExpiresAt <= Now)) return new(false);
        return new(true, confirmation.ConfirmedAt, tool.ExpiresAt);
    }
    public async Task ReconcileExecutionsAsync(CancellationToken ct)
    {
        if (actions is null) return;
        foreach (var location in await store.PendingExecutionsAsync(Now.AddSeconds(-30), 50, ct))
        {
            try
            {
                LessonAction action;
                await using (var session = await store.OpenAsync(location.SchoolId, location.UserId, true, ct))
                {
                    var tool = await session.FindAsync<ToolInvocation>(i => i.Id == location.PreviewId, ct);
                    if (tool is null || tool.Status is not (InvocationStatus.Executing or InvocationStatus.Unknown)) continue;
                    if (tool.RecoveryAfter > Now) continue;
                    action = FrozenAction(tool); tool.RecoveryAfter = Now.AddSeconds(30); tool.Revision++; await session.CommitAsync(ct);
                }
                var receipt = await actions.ResolveAsync(action, ct); await SaveExecutionResult(action, receipt, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { /* Retain the intent; the next bounded scan retries recovery only. */ }
        }
    }
}
