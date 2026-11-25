using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Exact saved advisory preview; not an execution permit.</summary>
public sealed record PreviewResponse(Guid PreviewId, Guid OperationId, string Kind, JsonElement Arguments, JsonElement Display, JsonElement SourceRevisions, string ArgumentsFingerprint, DateTimeOffset ExpiresAt, string Status, bool ExecutionAllowed = false);
