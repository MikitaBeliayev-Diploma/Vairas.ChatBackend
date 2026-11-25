using System.Text.Json;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed partial class ChatService
{
    private async Task<PreviewResponse> SavePreview(Guid school, Guid conversation, Guid runId, int sequence, string name, JsonElement arguments, OwnerPreview owner, ChatActor actor, CancellationToken ct)
    {
        if (owner.PreviewVersion != "scheduling-preview/1" || owner.Kind != (name == "preview_lesson_booking" ? "Booking" : "Rescheduling")) throw ChatErrors.Dependency("OWNER_PREVIEW_INVALID");
        _ = ChatRules.Canonical(Wire.Element(owner));
        (actor, var access) = await Admission(school, actor, ct);
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        var conversationRow = await Conversation(session, conversation, actor, access, ct); RequireModel(); await CurrentRun(session, conversation, runId, ct);
        if (await session.FindAsync<ToolInvocation>(i => i.ConversationId == conversation && i.Effect == ToolEffect.Write && (i.Status == InvocationStatus.Executing || i.Status == InvocationStatus.Unknown), ct) is not null) throw ChatErrors.Conflict("EXECUTION_IN_PROGRESS");
        foreach (var old in await session.ListAsync<ToolInvocation>(i => i.ConversationId == conversation && i.Effect == ToolEffect.Write && (i.Status == InvocationStatus.AwaitingConfirmation || i.Status == InvocationStatus.Confirmed), ct))
        { old.Status = InvocationStatus.Cancelled; old.ErrorCode = "PREVIEW_SUPERSEDED"; old.FinishedAt = Now; old.Revision++; }
        await session.FlushAsync(ct);
        var id = Guid.NewGuid(); var operation = Guid.NewGuid(); var expires = Now.AddSeconds(Options.PreviewSeconds);
        var envelope = Wire.Element(new { schema = "vairas-chat-preview-consent/1", actorIdentityId = actor.IdentityId, actorUserId = actor.UserId, schoolId = school, conversationId = conversation, toolInvocationId = id, toolName = name, toolContractVersion = "chat-tools/1", targetContext = "Scheduling", operationId = operation, ownerPreviewVersion = owner.PreviewVersion, kind = owner.Kind, arguments = owner.Arguments, sourceRevisions = owner.SourceRevisions, displayedPreview = owner.Display, expiresAt = expires });
        if (conversationRow.ExecutionProfile == Vairas.ConfirmedActions.LessonActions.Profile)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(envelope.GetRawText())!.AsObject();
            node["schema"] = "vairas-chat-confirmed-lesson-action/1";
            node["executionProfile"] = conversationRow.ExecutionProfile;
            node["actorSessionId"] = actor.SessionId;
            node["commandDigest"] = Vairas.ConfirmedActions.LessonActions.Digest(owner.Kind, owner.Arguments, owner.SourceRevisions);
            envelope = Wire.Element(node);
        }
        var tool = new ToolInvocation { Id = id, SchoolId = school, ConversationId = conversation, RunId = runId, SequenceNo = sequence, ToolName = name, Effect = ToolEffect.Write, TargetContext = "Scheduling", CanonicalArguments = ChatRules.Canonical(arguments), Preview = ChatRules.Canonical(envelope), ExpectedSourceRevisions = ChatRules.Canonical(owner.SourceRevisions), ActionDigest = ChatRules.Hash(envelope), ExpiresAt = expires, OperationId = operation, Status = InvocationStatus.AwaitingConfirmation, CreatedAt = Now };
        session.Add(tool); Fresh(actor, access); await session.CommitAsync(ct); return PreviewDto(tool);
    }
    private static PreviewResponse PreviewDto(ToolInvocation tool)
    {
        var saved = Wire.Read<JsonElement>(tool.Preview!);
        return new(tool.Id, tool.OperationId, saved.GetProperty("kind").GetString()!, saved.GetProperty("arguments"), saved.GetProperty("displayedPreview"), saved.GetProperty("sourceRevisions"), tool.ActionDigest!, tool.ExpiresAt!.Value, tool.Status.ToString());
    }
    private async Task<ToolInvocation> Preview(IChatSession session, Guid preview, ChatActor actor, SchoolAccess access, CancellationToken ct)
    {
        var tool = await session.FindAsync<ToolInvocation>(i => i.Id == preview && i.Effect == ToolEffect.Write, ct) ?? throw ChatErrors.Missing();
        await Conversation(session, tool.ConversationId, actor, access, ct);
        if (tool.ExpiresAt <= Now && tool.Status is InvocationStatus.AwaitingConfirmation or InvocationStatus.Confirmed) { tool.Status = InvocationStatus.Expired; tool.FinishedAt = Now; tool.Revision++; }
        return tool;
    }
    public async Task<PreviewResponse> PreviewAsync(Guid school, Guid preview, ChatActor actor, CancellationToken ct)
    {
        (actor, var access) = await Admission(school, actor, ct);
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        var tool = await Preview(session, preview, actor, access, ct); Fresh(actor, access); await session.CommitAsync(ct); return PreviewDto(tool);
    }
    public async Task<ApprovalResponse> ApproveAsync(Guid school, Guid preview, ApprovalRequest request, Guid operation, ChatActor actor, CancellationToken ct)
    {
        if (!request.Approved || request.OperationId != operation || operation == Guid.Empty) throw ChatErrors.Invalid("EXACT_APPROVAL_REQUIRED");
        (actor, var access) = await Admission(school, actor, ct);
        ToolInvocation snapshot;
        await using (var read = await store.OpenAsync(school, actor.UserId, true, ct))
        {
            snapshot = await Preview(read, preview, actor, access, ct);
            if (snapshot.OperationId != operation || snapshot.ActionDigest != request.ArgumentsFingerprint || Wire.Read<JsonElement>(snapshot.Preview!).GetProperty("actorIdentityId").GetGuid() != actor.IdentityId) throw ChatErrors.Conflict("CONFIRMATION_MISMATCH");
            var prior = await read.FindAsync<ActionConfirmation>(c => c.InvocationId == preview && c.ConversationId == snapshot.ConversationId, ct);
            if (prior is not null) { await read.CommitAsync(ct); return new(preview, preview, operation, prior.ConfirmedAt, snapshot.ExpiresAt!.Value); }
            if (snapshot.Status != InvocationStatus.AwaitingConfirmation) throw ChatErrors.Conflict("PREVIEW_NOT_CONFIRMABLE");
            await read.CommitAsync(ct);
        }
        var current = await owners.PreviewAsync(snapshot.ToolName, Wire.Read<JsonElement>(snapshot.CanonicalArguments), school, actor, ct);
        var saved = Wire.Read<JsonElement>(snapshot.Preview!);
        if (current.PreviewVersion != saved.GetProperty("ownerPreviewVersion").GetString() || ChatRules.Hash(current.Arguments) != ChatRules.Hash(saved.GetProperty("arguments")) || ChatRules.Hash(current.SourceRevisions) != ChatRules.Hash(saved.GetProperty("sourceRevisions")) || ChatRules.Hash(current.Display) != ChatRules.Hash(saved.GetProperty("displayedPreview"))) throw ChatErrors.Conflict("PREVIEW_STALE");
        (actor, access) = await Admission(school, actor, ct);
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        var tool = await Preview(session, preview, actor, access, ct);
        var confirmation = await session.FindAsync<ActionConfirmation>(c => c.InvocationId == preview && c.ConversationId == tool.ConversationId, ct);
        if (confirmation is not null) return new(preview, preview, operation, confirmation.ConfirmedAt, tool.ExpiresAt!.Value);
        if (tool.Status != InvocationStatus.AwaitingConfirmation || tool.Revision != snapshot.Revision || ChatRules.Hash(Wire.Read<JsonElement>(tool.Preview!)) != request.ArgumentsFingerprint) throw ChatErrors.Conflict("PREVIEW_NOT_CONFIRMABLE");
        confirmation = new() { SchoolId = school, ConversationId = tool.ConversationId, InvocationId = preview, ConfirmedByUserId = actor.UserId, ConfirmedDigest = tool.ActionDigest!, ConfirmedAt = Now };
        session.Add(confirmation); await session.FlushAsync(ct);
        tool.Status = InvocationStatus.Confirmed; tool.Revision++; Fresh(actor, access); await session.CommitAsync(ct);
        return new(preview, preview, operation, confirmation.ConfirmedAt, tool.ExpiresAt!.Value);
    }
}
