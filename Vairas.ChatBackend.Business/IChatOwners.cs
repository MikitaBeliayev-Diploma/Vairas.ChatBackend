using System.Linq.Expressions;
using System.Text.Json;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public interface IChatOwners
{
    Task<ChatActor> ReadmitAsync(ChatActor actor, CancellationToken ct);
    Task<SchoolAccess> AccessAsync(Guid school, ChatActor actor, CancellationToken ct);
    Task<JsonElement> ReadToolAsync(string name, JsonElement arguments, Guid school, ChatActor actor, CancellationToken ct);
    Task<OwnerPreview> PreviewAsync(string name, JsonElement arguments, Guid school, ChatActor actor, CancellationToken ct);
}
