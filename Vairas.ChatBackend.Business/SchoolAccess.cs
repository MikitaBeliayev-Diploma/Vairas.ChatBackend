using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed record SchoolAccess(Guid SchoolId, Guid ActorUserId, bool Allowed, bool CanAudit, string AccessStamp, DateTimeOffset ValidUntil);
