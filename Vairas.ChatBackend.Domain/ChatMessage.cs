namespace Vairas.ChatBackend.Domain;

public sealed class ChatMessage : ISchoolRow
{
    public Guid SchoolId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid Id { get; set; }
    public long SequenceNo { get; set; }
    public MessageRole Role { get; set; }
    public string Content { get; set; } = "";
    public Guid? ClientRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
