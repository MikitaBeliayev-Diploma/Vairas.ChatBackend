namespace Vairas.ChatBackend.Domain;

public sealed class Conversation : ISchoolRow
{
    public string ExecutionProfile { get; set; } = "PreviewOnlyV1";
    public Guid SchoolId { get; set; }
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public Guid CreationRequestId { get; set; }
    public string AccessStamp { get; set; } = "";
    public string? Title { get; set; }
    public ConversationStatus Status { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? ArchiveRequestId { get; set; }
    public long? ArchiveExpectedRevision { get; set; }
}
