namespace Vairas.ChatBackend.Domain;

public sealed class AssistantRun : ISchoolRow
{
    public Guid SchoolId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid Id { get; set; }
    public Guid TriggerMessageId { get; set; }
    public Guid? ResponseMessageId { get; set; }
    public int AttemptNo { get; set; } = 1;
    public Guid ModelDeploymentId { get; set; }
    public string PromptVersion { get; set; } = "chat-prompt/1";
    public string ExecutionProfile { get; set; } = "PreviewOnlyV1";
    public DateTimeOffset DeadlineAt { get; set; }
    public RunStatus Status { get; set; }
    public long Revision { get; set; } = 1;
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
