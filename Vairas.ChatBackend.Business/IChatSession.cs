using System.Linq.Expressions;
using System.Text.Json;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public interface IChatSession : IAsyncDisposable
{
    Task<T?> FindAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct) where T : class, ISchoolRow;
    Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct, int limit = 100, bool newestFirst = false) where T : class, ISchoolRow;
    Task<ModelDeployment?> ModelAsync(CancellationToken ct);
    void Add<T>(T row) where T : class, ISchoolRow;
    Task FlushAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
}
