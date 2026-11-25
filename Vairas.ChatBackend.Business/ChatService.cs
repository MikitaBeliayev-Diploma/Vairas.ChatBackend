using System.Text.Json;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed partial class ChatService(IChatStore store, IChatOwners owners, IModelAdapter model, IChatSettings settings, TimeProvider clock, ILessonActionDispatcher? actions = null)
{
    private ChatOptions Options => settings.Current;
    private DateTimeOffset Now => ChatRules.Instant(clock.GetUtcNow());
    private async Task<(ChatActor Actor, SchoolAccess Access)> Admission(Guid school, ChatActor actor, CancellationToken ct)
    {
        ChatRules.Id(school);
        actor = await owners.ReadmitAsync(actor, ct);
        var access = await owners.AccessAsync(school, actor, ct);
        if (access.SchoolId != school || access.ActorUserId != actor.UserId || string.IsNullOrWhiteSpace(access.AccessStamp)) throw ChatErrors.Dependency("INVALID_AUTHORITY");
        ChatRules.Fresh(actor.ValidUntil, Now); ChatRules.Fresh(access.ValidUntil, Now);
        return (actor, access);
    }
    private void Fresh(ChatActor actor, SchoolAccess access)
    {
        ChatRules.Fresh(actor.ValidUntil, Now); ChatRules.Fresh(access.ValidUntil, Now);
    }
    private async Task<Conversation> Conversation(IChatSession session, Guid id, ChatActor actor, SchoolAccess access, CancellationToken ct)
    {
        ChatRules.Id(id); Fresh(actor, access);
        var conversation = await session.FindAsync<Conversation>(c => c.Id == id && c.OwnerUserId == actor.UserId, ct) ?? throw ChatErrors.Missing();
        if (!access.Allowed || conversation.AccessStamp != access.AccessStamp)
        {
            if (conversation.Status == ConversationStatus.Open) { await Close(session, conversation, "ACCESS_CHANGED", ct); await session.CommitAsync(ct); }
            throw ChatErrors.Missing();
        }
        return conversation;
    }
    private async Task Close(IChatSession session, Conversation conversation, string reason, CancellationToken ct)
    {
        foreach (var invocation in await session.ListAsync<ToolInvocation>(i => i.ConversationId == conversation.Id && !ChatRules.TerminalInvocations.Contains(i.Status), ct, 1000))
        {
            if (invocation.Status == InvocationStatus.Unknown) continue;
            invocation.Status = invocation.Effect == ToolEffect.Write && invocation.Status == InvocationStatus.Executing ? InvocationStatus.Unknown : invocation.Status == InvocationStatus.Executing ? InvocationStatus.Failed : InvocationStatus.Cancelled;
            invocation.ErrorCode = reason; invocation.FinishedAt = invocation.Status == InvocationStatus.Unknown ? null : Now; invocation.Revision++;
        }
        foreach (var run in await session.ListAsync<AssistantRun>(r => r.ConversationId == conversation.Id && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.AwaitingConfirmation), ct))
        { run.Status = RunStatus.Cancelled; run.ErrorCode = reason; run.FinishedAt = Now; run.Revision++; }
        await session.FlushAsync(ct);
        conversation.Status = ConversationStatus.Archived; conversation.ArchivedAt = Now; conversation.Revision++;
    }
    private async Task RecoverExpired(IChatSession session, Guid conversation, CancellationToken ct)
    {
        foreach (var run in await session.ListAsync<AssistantRun>(r => r.ConversationId == conversation && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running) && r.DeadlineAt <= Now, ct))
        {
            foreach (var tool in await session.ListAsync<ToolInvocation>(i => i.RunId == run.Id && !ChatRules.TerminalInvocations.Contains(i.Status), ct))
            { if (tool.Status == InvocationStatus.Unknown) continue; tool.Status = tool.Effect == ToolEffect.Write && tool.Status == InvocationStatus.Executing ? InvocationStatus.Unknown : tool.Effect == ToolEffect.Write ? InvocationStatus.Cancelled : InvocationStatus.Failed; tool.ErrorCode = "RUN_INTERRUPTED"; tool.FinishedAt = tool.Status == InvocationStatus.Unknown ? null : Now; tool.Revision++; }
            run.Status = RunStatus.Failed; run.ErrorCode = "RUN_INTERRUPTED"; run.FinishedAt = Now; run.Revision++;
        }
        await ExpirePreviews(session, conversation, ct);
        await session.FlushAsync(ct);
    }
    private async Task ExpirePreviews(IChatSession session, Guid conversation, CancellationToken ct)
    {
        foreach (var tool in await session.ListAsync<ToolInvocation>(i => i.ConversationId == conversation && i.Effect == ToolEffect.Write && (i.Status == InvocationStatus.AwaitingConfirmation || i.Status == InvocationStatus.Confirmed) && i.ExpiresAt <= Now, ct))
        { tool.Status = InvocationStatus.Expired; tool.FinishedAt = Now; tool.Revision++; }
    }
    private static SessionResponse SessionDto(Conversation c) => new(c.Id, c.SchoolId, c.Status.ToString(), c.Revision, c.CreatedAt, c.ExecutionProfile);

    public async Task<SessionResponse> CreateAsync(CreateSessionRequest request, Guid operation, ChatActor actor, CancellationToken ct)
    {
        ChatRules.Id(operation);
        if (request.ExecutionProfile is not ("PreviewOnlyV1" or "ConfirmedLessonActionsV1")) throw ChatErrors.Invalid("EXECUTION_PROFILE_INVALID");
        if (request.ExecutionProfile == "ConfirmedLessonActionsV1" && !Options.ConfirmedLessonActionsEnabled) throw ChatErrors.Dependency("LESSON_ACTIONS_DISABLED");
        (actor, var access) = await Admission(request.SchoolId, actor, ct);
        if (!access.Allowed) throw ChatErrors.Missing();
        await using var session = await store.OpenAsync(request.SchoolId, actor.UserId, true, ct); Fresh(actor, access);
        var existing = await session.FindAsync<Conversation>(c => c.OwnerUserId == actor.UserId && c.CreationRequestId == operation, ct);
        if (existing is not null)
        {
            if (existing.ExecutionProfile != request.ExecutionProfile) throw ChatErrors.Conflict("IDEMPOTENCY_KEY_REUSED");
            return SessionDto(await Conversation(session, existing.Id, actor, access, ct));
        }
        var conversation = new Conversation { Id = Guid.NewGuid(), SchoolId = request.SchoolId, OwnerUserId = actor.UserId, CreationRequestId = operation, AccessStamp = access.AccessStamp, ExecutionProfile = request.ExecutionProfile, CreatedAt = Now };
        session.Add(conversation); await session.CommitAsync(ct); return SessionDto(conversation);
    }
    public async Task<IReadOnlyList<SessionResponse>> ListAsync(Guid school, DateTimeOffset? before, int limit, ChatActor actor, CancellationToken ct, Guid? beforeId = null)
    {
        if (limit is < 1 or > 100 || before.HasValue != beforeId.HasValue || beforeId == Guid.Empty) throw ChatErrors.Invalid();
        (actor, var access) = await Admission(school, actor, ct); if (!access.Allowed) throw ChatErrors.Missing();
        await using var session = await store.OpenAsync(school, actor.UserId, false, ct); Fresh(actor, access);
        return (await session.ListAsync<Conversation>(c => c.OwnerUserId == actor.UserId && c.AccessStamp == access.AccessStamp && (before == null || c.CreatedAt < before || c.CreatedAt == before && c.Id.CompareTo(beforeId!.Value) < 0), ct, limit, true)).Select(SessionDto).ToArray();
    }
    public async Task<SessionResponse> GetAsync(Guid school, Guid conversation, ChatActor actor, CancellationToken ct)
    {
        (actor, var access) = await Admission(school, actor, ct);
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        return SessionDto(await Conversation(session, conversation, actor, access, ct));
    }
    public async Task<IReadOnlyList<MessageResponse>> HistoryAsync(Guid school, Guid conversation, long after, int limit, ChatActor actor, CancellationToken ct)
    {
        if (after < 0 || limit is < 1 or > 100) throw ChatErrors.Invalid();
        (actor, var access) = await Admission(school, actor, ct);
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        await Conversation(session, conversation, actor, access, ct);
        return (await session.ListAsync<ChatMessage>(m => m.ConversationId == conversation && m.SequenceNo > after, ct, limit)).Select(m => new MessageResponse(m.Id, m.SequenceNo, m.Role.ToString(), m.Content, m.CreatedAt)).ToArray();
    }
    public async Task<SessionResponse> ArchiveAsync(Guid school, Guid conversation, ArchiveRequest request, Guid operation, ChatActor actor, CancellationToken ct)
    {
        ChatRules.Id(operation); (actor, var access) = await Admission(school, actor, ct);
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        var c = await Conversation(session, conversation, actor, access, ct);
        if (c.Status == ConversationStatus.Archived)
        {
            if (c.ArchiveRequestId != operation || c.ArchiveExpectedRevision != request.ExpectedRevision) throw ChatErrors.Conflict("IDEMPOTENCY_KEY_REUSED");
            return SessionDto(c);
        }
        if (c.Revision != request.ExpectedRevision) throw ChatErrors.Conflict();
        if (await session.FindAsync<Conversation>(other => other.OwnerUserId == actor.UserId && other.ArchiveRequestId == operation, ct) is not null) throw ChatErrors.Conflict("IDEMPOTENCY_KEY_REUSED");
        await Close(session, c, "CONVERSATION_ARCHIVED", ct);
        c.ArchiveRequestId = operation; c.ArchiveExpectedRevision = request.ExpectedRevision;
        Fresh(actor, access); await session.CommitAsync(ct); return SessionDto(c);
    }
    public async Task<RunResponse> RunAsync(Guid school, Guid conversation, Guid run, ChatActor actor, CancellationToken ct)
    {
        (actor, var access) = await Admission(school, actor, ct);
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        await Conversation(session, conversation, actor, access, ct); await RecoverExpired(session, conversation, ct);
        var r = await session.FindAsync<AssistantRun>(r => r.ConversationId == conversation && r.Id == run, ct) ?? throw ChatErrors.Missing();
        await session.CommitAsync(ct); return new(r.Id, r.Status.ToString(), r.ErrorCode, r.ResponseMessageId);
    }
    public async Task<IReadOnlyList<AuditEntry>> AuditAsync(Guid school, int limit, ChatActor actor, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw ChatErrors.Invalid();
        (actor, var access) = await Admission(school, actor, ct); if (!access.Allowed || !access.CanAudit) throw ChatErrors.Missing();
        Fresh(actor, access); return await store.AuditAsync(school, actor.UserId, limit, ct);
    }
    public Task<ConfirmationEvidence> ConfirmationAsync(ConfirmationQuery query, CancellationToken ct)
    {
        ChatRules.Id(query.SchoolId); ChatRules.Id(query.SessionId); ChatRules.Id(query.PreviewId); ChatRules.Id(query.IdentityId); ChatRules.Id(query.OperationId);
        if (query.ArgumentsFingerprint is not { Length: 64 }) throw ChatErrors.Invalid();
        return store.ConfirmationAsync(query, ct);
    }
}
