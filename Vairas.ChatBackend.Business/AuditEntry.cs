using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>School audit metadata without conversation identifiers, text or arguments.</summary>
public sealed record AuditEntry(Guid OperationId, string Tool, string Outcome, DateTimeOffset RecordedAt);
