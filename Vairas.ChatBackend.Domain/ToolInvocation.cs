namespace Vairas.ChatBackend.Domain;

public sealed class ToolInvocation : ISchoolRow
{
    public DateTimeOffset? RecoveryAfter { get; set; }
    public Guid SchoolId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int SequenceNo { get; set; }
    public string ToolName { get; set; } = "";
    public string ToolContractVersion { get; set; } = "chat-tools/1";
    public ToolEffect Effect { get; set; }
    public string TargetContext { get; set; } = "";
    public string CanonicalArguments { get; set; } = "{}";
    public string? Preview { get; set; }
    public string? ExpectedSourceRevisions { get; set; }
    public string? ActionDigest { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public Guid OperationId { get; set; }
    public InvocationStatus Status { get; set; }
    public long Revision { get; set; } = 1;
    public string? ResultReference { get; set; }
    public string? ResultSummary { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
