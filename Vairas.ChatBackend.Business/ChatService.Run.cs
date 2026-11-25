using System.Text;
using System.Text.Json;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed partial class ChatService
{
    public async Task<ChatReply> MessageAsync(Guid school, Guid conversation, SendMessageRequest request, Guid operation, ChatActor actor, CancellationToken ct)
    {
        ChatRules.Id(operation); ChatRules.Text(request.Text);
        if (request.SchoolId is Guid specified && specified != school) throw ChatErrors.Missing();
        (actor, var access) = await Admission(school, actor, ct);
        Guid runId; ModelDeployment deployment; List<ModelMessage> history;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(Options.RunSeconds));
        await using (var session = await store.OpenAsync(school, actor.UserId, true, ct))
        {
            var c = await Conversation(session, conversation, actor, access, ct);
            if (c.Status != ConversationStatus.Open) throw ChatErrors.Conflict("CONVERSATION_ARCHIVED");
            await RecoverExpired(session, conversation, ct);
            var prior = await session.FindAsync<ChatMessage>(m => m.ConversationId == conversation && m.ClientRequestId == operation, ct);
            if (prior is not null)
            {
                if (prior.Content != request.Text) throw ChatErrors.Conflict("IDEMPOTENCY_KEY_REUSED");
                var run = await session.FindAsync<AssistantRun>(r => r.ConversationId == conversation && r.TriggerMessageId == prior.Id, ct) ?? throw ChatErrors.Dependency("RUN_STATE_INVALID");
                var replay = await Reply(session, conversation, run, ct);
                await session.CommitAsync(ct); return replay;
            }
            if (await session.FindAsync<AssistantRun>(r => r.ConversationId == conversation && (r.Status == RunStatus.Queued || r.Status == RunStatus.Running || r.Status == RunStatus.AwaitingConfirmation), ct) is not null) throw ChatErrors.Conflict("RUN_IN_PROGRESS");
            RequireModel(); deployment = await session.ModelAsync(ct) ?? throw ChatErrors.Dependency("MODEL_NOT_CONFIGURED");
            var previous = await session.ListAsync<ChatMessage>(m => m.ConversationId == conversation && m.Role == MessageRole.User, ct, 10, true);
            history = [];
            var bytes = 0;
            foreach (var message in previous)
            {
                var length = Encoding.UTF8.GetByteCount(message.Content); if (bytes + length > 16384) break;
                bytes += length; history.Insert(0, new("user", message.Content));
            }
            history.Add(new("user", request.Text));
            var latest = (await session.ListAsync<ChatMessage>(m => m.ConversationId == conversation, ct, 1, true)).SingleOrDefault();
            var input = new ChatMessage { Id = Guid.NewGuid(), SchoolId = school, ConversationId = conversation, SequenceNo = (latest?.SequenceNo ?? 0) + 1, Role = MessageRole.User, Content = request.Text, ClientRequestId = operation, CreatedAt = Now };
            session.Add(input); await session.FlushAsync(ct);
            var created = new AssistantRun { Id = Guid.NewGuid(), SchoolId = school, ConversationId = conversation, TriggerMessageId = input.Id, ModelDeploymentId = deployment.Id, ExecutionProfile = c.ExecutionProfile, CreatedAt = Now, DeadlineAt = Now.AddSeconds(Options.RunSeconds) };
            session.Add(created); await session.FlushAsync(ct);
            created.Status = RunStatus.Running; created.StartedAt = Now; created.Revision++; runId = created.Id;
            Fresh(actor, access); await session.CommitAsync(ct);
        }
        try
        {
            var calls = 0; var toolSequence = 0; var previewCreated = false;
            long inputTokens = 0, outputTokens = 0; var inputKnown = true; var outputKnown = true;
            for (var iteration = 0; iteration <= Options.MaxToolCalls; iteration++)
            {
                deadline.Token.ThrowIfCancellationRequested(); RequireModel();
                (actor, access) = await Admission(school, actor, deadline.Token);
                await GuardRun(school, conversation, runId, actor, access, deadline.Token);
                using var modelDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); modelDeadline.CancelAfter(TimeSpan.FromSeconds(Options.ModelSeconds));
                var turn = await model.GenerateAsync(deployment, history, ToolCatalog.All, modelDeadline.Token);
                if (turn.InputTokens is long input && input >= 0) inputTokens = checked(inputTokens + input); else inputKnown = false;
                if (turn.OutputTokens is long output && output >= 0) outputTokens = checked(outputTokens + output); else outputKnown = false;
                if (turn.Calls.Count == 0)
                {
                    if (string.IsNullOrWhiteSpace(turn.Text) || Encoding.UTF8.GetByteCount(turn.Text) > 32768) throw ChatErrors.Dependency("MODEL_RESPONSE_INVALID");
                    (actor, access) = await Admission(school, actor, deadline.Token);
                    await using var finish = await store.OpenAsync(school, actor.UserId, true, deadline.Token);
                    await Conversation(finish, conversation, actor, access, deadline.Token); RequireModel();
                    var running = await CurrentRun(finish, conversation, runId, deadline.Token);
                    if ((await finish.ModelAsync(deadline.Token))?.Id != deployment.Id) throw ChatErrors.Dependency("MODEL_DISABLED");
                    var latest = (await finish.ListAsync<ChatMessage>(m => m.ConversationId == conversation, deadline.Token, 1, true)).Single();
                    var response = new ChatMessage { Id = Guid.NewGuid(), SchoolId = school, ConversationId = conversation, Role = MessageRole.Assistant, Content = turn.Text, SequenceNo = latest.SequenceNo + 1, CreatedAt = Now };
                    finish.Add(response); await finish.FlushAsync(deadline.Token);
                    running.ResponseMessageId = response.Id; running.Status = RunStatus.Completed; running.FinishedAt = Now; running.Revision++;
                    running.InputTokens = inputKnown ? inputTokens : null; running.OutputTokens = outputKnown ? outputTokens : null;
                    var reply = await Reply(finish, conversation, running, deadline.Token);
                    Fresh(actor, access); await finish.CommitAsync(deadline.Token); return reply;
                }
                if (turn.Calls.Count > Options.MaxToolCalls - calls || turn.Calls.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != turn.Calls.Count || turn.Calls.Any(c => string.IsNullOrWhiteSpace(c.Id))) throw ChatErrors.Invalid("TOOL_LIMIT_EXCEEDED");
                history.Add(new("assistant", null, turn.Calls));
                foreach (var call in turn.Calls)
                {
                    calls++; var definition = ToolCatalog.Definition(call.Name); var arguments = ToolCatalog.Normalize(call.Name, call.Arguments, school);
                    if (definition.Preview && previewCreated) throw ChatErrors.Invalid("PREVIEW_LIMIT_EXCEEDED");
                    (actor, access) = await Admission(school, actor, deadline.Token); if (!access.Allowed) throw ChatErrors.Missing();
                    JsonElement result;
                    if (definition.Preview)
                    {
                        var owner = await owners.PreviewAsync(call.Name, arguments, school, actor, deadline.Token);
                        var preview = await SavePreview(school, conversation, runId, ++toolSequence, call.Name, arguments, owner, actor, deadline.Token);
                        previewCreated = true; result = Wire.Element(preview);
                    }
                    else result = await ReadTool(school, conversation, runId, ++toolSequence, call.Name, arguments, actor, deadline.Token);
                    if (Encoding.UTF8.GetByteCount(result.GetRawText()) > 32768) throw ChatErrors.Dependency("TOOL_RESULT_TOO_LARGE");
                    history.Add(new("tool", result.GetRawText(), CallId: call.Id));
                }
            }
            throw ChatErrors.Invalid("TOOL_LIMIT_EXCEEDED");
        }
        catch (Exception exception)
        {
            var code = exception is BusinessException chat ? chat.Code : exception is OperationCanceledException ? ct.IsCancellationRequested ? "REQUEST_CANCELLED" : "RUN_TIMEOUT" : "MODEL_FAILURE";
            await FailRun(school, conversation, runId, actor.UserId, code);
            if (exception is BusinessException) throw;
            if (exception is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw ChatErrors.Dependency(code);
        }
    }
    private void RequireModel() { if (Options.EmergencyDisabled || !model.Enabled) throw ChatErrors.Dependency("MODEL_DISABLED"); }
    private async Task<AssistantRun> CurrentRun(IChatSession session, Guid conversation, Guid run, CancellationToken ct)
    {
        var current = await session.FindAsync<AssistantRun>(r => r.ConversationId == conversation && r.Id == run, ct) ?? throw ChatErrors.Missing();
        if (current.Status != RunStatus.Running || current.DeadlineAt <= Now) throw ChatErrors.Conflict("RUN_CLOSED");
        return current;
    }
    private async Task GuardRun(Guid school, Guid conversation, Guid run, ChatActor actor, SchoolAccess access, CancellationToken ct)
    {
        await using var session = await store.OpenAsync(school, actor.UserId, true, ct);
        await Conversation(session, conversation, actor, access, ct);
        await CurrentRun(session, conversation, run, ct); if (await session.ModelAsync(ct) is null) throw ChatErrors.Dependency("MODEL_DISABLED");
    }
    private static async Task<ChatReply> Reply(IChatSession session, Guid conversation, AssistantRun run, CancellationToken ct)
    {
        if (run.Status != RunStatus.Completed) throw ChatErrors.Conflict(ChatRules.Active(run.Status) ? "RUN_IN_PROGRESS" : run.ErrorCode ?? "RUN_FAILED");
        var message = await session.FindAsync<ChatMessage>(m => m.ConversationId == conversation && m.Id == run.ResponseMessageId, ct) ?? throw ChatErrors.Dependency("RUN_STATE_INVALID");
        var preview = (await session.ListAsync<ToolInvocation>(i => i.RunId == run.Id && i.Effect == ToolEffect.Write, ct, 1)).SingleOrDefault();
        return new(message.Id, message.Content, preview?.Id, run.Id);
    }
    private async Task<JsonElement> ReadTool(Guid school, Guid conversation, Guid runId, int sequence, string name, JsonElement arguments, ChatActor actor, CancellationToken ct)
    {
        Guid invocation;
        await using (var session = await store.OpenAsync(school, actor.UserId, true, ct))
        {
            RequireModel(); await CurrentRun(session, conversation, runId, ct);
            var tool = new ToolInvocation { Id = Guid.NewGuid(), SchoolId = school, ConversationId = conversation, RunId = runId, SequenceNo = sequence, ToolName = name, TargetContext = name == "get_student_training_progress" ? "LearningTracking" : name == "get_my_enrollments" ? "SchoolManagement" : "Scheduling", CanonicalArguments = ChatRules.Canonical(arguments), OperationId = Guid.NewGuid(), Status = InvocationStatus.Prepared, CreatedAt = Now };
            session.Add(tool); await session.FlushAsync(ct); tool.Status = InvocationStatus.Executing; tool.StartedAt = Now; tool.Revision++; invocation = tool.Id; await session.CommitAsync(ct);
        }
        var result = await owners.ReadToolAsync(name, arguments, school, actor, ct);
        if (Encoding.UTF8.GetByteCount(result.GetRawText()) > 32768) throw ChatErrors.Dependency("TOOL_RESULT_TOO_LARGE");
        await using (var finish = await store.OpenAsync(school, actor.UserId, true, ct))
        {
            RequireModel(); await CurrentRun(finish, conversation, runId, ct);
            var tool = await finish.FindAsync<ToolInvocation>(t => t.Id == invocation, ct) ?? throw ChatErrors.Missing();
            if (tool.Status != InvocationStatus.Executing) throw ChatErrors.Conflict("RUN_CLOSED");
            tool.Status = InvocationStatus.Succeeded; tool.FinishedAt = Now; tool.Revision++;
            // Owner facts are transient model context; they are not retained as tool-result history.
            await finish.CommitAsync(ct);
        }
        return result;
    }
    private async Task FailRun(Guid school, Guid conversation, Guid runId, Guid user, string code)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await using var session = await store.OpenAsync(school, user, true, cleanup.Token);
            var run = await session.FindAsync<AssistantRun>(r => r.ConversationId == conversation && r.Id == runId, cleanup.Token);
            if (run is null || !ChatRules.Active(run.Status)) return;
            foreach (var tool in await session.ListAsync<ToolInvocation>(t => t.RunId == runId && !ChatRules.TerminalInvocations.Contains(t.Status), cleanup.Token))
            { if (tool.Status == InvocationStatus.Unknown) continue; tool.Status = tool.Effect == ToolEffect.Write && tool.Status == InvocationStatus.Executing ? InvocationStatus.Unknown : tool.Effect == ToolEffect.Read ? InvocationStatus.Failed : InvocationStatus.Cancelled; tool.ErrorCode = code; tool.FinishedAt = tool.Status == InvocationStatus.Unknown ? null : Now; tool.Revision++; }
            run.Status = RunStatus.Failed; run.ErrorCode = code; run.FinishedAt = Now; run.Revision++; await session.CommitAsync(cleanup.Token);
        }
        catch (Exception) { /* A later authenticated access performs deadline-based recovery. */ }
    }
}
