using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Exact human approval; it never requests dispatch.</summary>
public sealed record ApprovalRequest(Guid OperationId, string ArgumentsFingerprint, bool Approved);
