using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Recorded intent, with execution explicitly disabled in v1.</summary>
public sealed record ApprovalResponse(Guid ConfirmationId, Guid PreviewId, Guid OperationId, DateTimeOffset ConfirmedAt, DateTimeOffset ExpiresAt, bool ExecutionAllowed = false);
