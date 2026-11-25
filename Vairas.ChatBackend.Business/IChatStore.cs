using System.Linq.Expressions;
using System.Text.Json;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public interface IChatStore
{
    Task<IReadOnlyList<ExecutionLocation>> PendingExecutionsAsync(DateTimeOffset before, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<ExecutionLocation>>([]);
    Task<IChatSession> OpenAsync(Guid school, Guid user, bool write, CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> AuditAsync(Guid school, Guid user, int limit, CancellationToken ct);
    Task<ConfirmationEvidence> ConfirmationAsync(ConfirmationQuery query, CancellationToken ct);
}
