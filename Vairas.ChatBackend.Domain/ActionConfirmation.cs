namespace Vairas.ChatBackend.Domain;

public sealed class ActionConfirmation : ISchoolRow
{
    public Guid SchoolId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid InvocationId { get; set; }
    public Guid ConfirmedByUserId { get; set; }
    public string ConfirmedDigest { get; set; } = "";
    public DateTimeOffset ConfirmedAt { get; set; }
}
