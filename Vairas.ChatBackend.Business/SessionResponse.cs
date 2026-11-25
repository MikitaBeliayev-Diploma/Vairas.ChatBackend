using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Private conversation metadata.</summary>
public sealed record SessionResponse(Guid SessionId, Guid SchoolId, string Status, long Revision, DateTimeOffset CreatedAt, string ExecutionProfile = "PreviewOnlyV1");
