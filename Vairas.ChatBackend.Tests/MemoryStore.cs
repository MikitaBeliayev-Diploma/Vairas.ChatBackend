using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;

namespace Vairas.ChatBackend.Tests;

public sealed class MemoryStore : IChatStore
{
    private Dictionary<Type, List<object>> rows = [];
    private readonly SemaphoreSlim gate = new(1);
    public ModelDeployment Model { get; } = new() { Id = Guid.NewGuid(), Enabled = true, Provider = "Fixture", DeploymentName = "synthetic", Region = "local", EndpointConfigKey = "Model:Endpoint", CredentialSecretRef = "Model:Credential", ConfigurationVersion = 1, ModelName = "synthetic", CreatedAt = DateTimeOffset.UtcNow };
    public IReadOnlyList<T> Rows<T>() => rows.TryGetValue(typeof(T), out var list) ? list.Cast<T>().ToArray() : [];
    public async Task<IChatSession> OpenAsync(Guid school, Guid user, bool write, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        var copy = rows.ToDictionary(p => p.Key, p => p.Value.Select(v => JsonSerializer.Deserialize(JsonSerializer.Serialize(v, p.Key, Wire.Json), p.Key, Wire.Json)!).ToList());
        if (!write) gate.Release(); return new Session(this, copy, school, user, write);
    }
    public Task<IReadOnlyList<AuditEntry>> AuditAsync(Guid school, Guid user, int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<AuditEntry>>(Rows<ToolInvocation>().Where(i => i.SchoolId == school && i.Effect == ToolEffect.Write).OrderByDescending(i => i.CreatedAt).Take(limit).Select(i => new AuditEntry(i.OperationId, i.ToolName, i.Status.ToString(), i.CreatedAt)).ToArray());
    public Task<ConfirmationEvidence> ConfirmationAsync(ConfirmationQuery query, CancellationToken ct)
    {
        var invocation = Rows<ToolInvocation>().SingleOrDefault(i => i.SchoolId == query.SchoolId && i.ConversationId == query.SessionId && i.Id == query.PreviewId && i.OperationId == query.OperationId && i.ActionDigest == query.ArgumentsFingerprint && i.Status == InvocationStatus.Confirmed);
        var confirmation = Rows<ActionConfirmation>().SingleOrDefault(c => c.SchoolId == query.SchoolId && c.ConversationId == query.SessionId && c.InvocationId == query.PreviewId);
        var identity = invocation is null ? Guid.Empty : Wire.Read<JsonElement>(invocation.Preview!).GetProperty("actorIdentityId").GetGuid();
        return Task.FromResult(invocation is not null && confirmation is not null && identity == query.IdentityId ? new ConfirmationEvidence(true, false, confirmation.ConfirmedAt, invocation.ExpiresAt) : new(false));
    }
    public Task<IReadOnlyList<ExecutionLocation>> PendingExecutionsAsync(DateTimeOffset before,int limit,CancellationToken ct) => Task.FromResult<IReadOnlyList<ExecutionLocation>>(Rows<ToolInvocation>().Where(t => t.Status == InvocationStatus.Unknown || t.Status == InvocationStatus.Executing && t.StartedAt <= before).Select(t => new ExecutionLocation(t.SchoolId, Rows<Conversation>().Single(c => c.Id == t.ConversationId).OwnerUserId, t.Id)).Take(limit).ToArray());
    private sealed class Session(MemoryStore owner, Dictionary<Type, List<object>> rows, Guid school, Guid user, bool write) : IChatSession
    {
        private List<object> Table<T>() => rows.TryGetValue(typeof(T), out var list) ? list : rows[typeof(T)] = [];
        private IEnumerable<T> Visible<T>() where T : class, ISchoolRow
        {
            var conversations = Table<Conversation>().Cast<Conversation>().Where(c => c.SchoolId == school && c.OwnerUserId == user).Select(c => c.Id).ToArray();
            return Table<T>().Cast<T>().Where(r => r.SchoolId == school && (r is Conversation c ? c.OwnerUserId == user : conversations.Contains((Guid)typeof(T).GetProperty("ConversationId")!.GetValue(r)!)));
        }
        public Task<T?> FindAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct) where T : class, ISchoolRow => Task.FromResult(Visible<T>().SingleOrDefault(predicate.Compile()));
        public Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct, int limit = 100, bool newestFirst = false) where T : class, ISchoolRow
        {
            var query = Visible<T>().Where(predicate.Compile());
            Func<T, IComparable> key = r => r is ChatMessage m ? m.SequenceNo : (DateTimeOffset)typeof(T).GetProperty("CreatedAt")!.GetValue(r)!;
            Func<T, Guid> id = r => (Guid)typeof(T).GetProperty("Id")!.GetValue(r)!;
            return Task.FromResult<IReadOnlyList<T>>((newestFirst ? query.OrderByDescending(key).ThenByDescending(id) : query.OrderBy(key).ThenBy(id)).Take(limit).ToArray());
        }
        public Task<ModelDeployment?> ModelAsync(CancellationToken ct) => Task.FromResult(owner.Model.Enabled ? owner.Model : null);
        public void Add<T>(T row) where T : class, ISchoolRow { if (row.SchoolId != school) throw ChatErrors.Missing(); Table<T>().Add(row); }
        public Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public Task CommitAsync(CancellationToken ct) { if (write) owner.rows = rows; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { if (write) owner.gate.Release(); return ValueTask.CompletedTask; }
    }
}
