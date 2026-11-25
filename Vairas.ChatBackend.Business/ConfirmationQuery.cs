using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Restricted workload query for an exact saved confirmation.</summary>
public sealed record ConfirmationQuery(Guid SchoolId, Guid SessionId, Guid PreviewId, Guid IdentityId, Guid OperationId, string ArgumentsFingerprint);
