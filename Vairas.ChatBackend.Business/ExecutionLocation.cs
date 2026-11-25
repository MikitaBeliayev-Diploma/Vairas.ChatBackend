using System.Text.Json;
using Vairas.ChatBackend.Domain;
using Vairas.ConfirmedActions;
namespace Vairas.ChatBackend.Business;

public sealed record ExecutionLocation(Guid SchoolId, Guid UserId, Guid PreviewId);
