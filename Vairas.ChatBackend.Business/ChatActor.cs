using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed record ChatActor(Guid IdentityId, Guid SessionId, Guid UserId, string UserBearer, DateTimeOffset ValidUntil);
