using System.Buffers.Binary;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Persistance;
public sealed class ChatStore(IDbContextFactory<ChatDbContext> factory, StoreOptions options) : IChatStore
{
    public async Task<IChatSession> OpenAsync(Guid school, Guid user, bool write, CancellationToken ct)
    {
        ChatRules.Id(school); ChatRules.Id(user);
        var db = await factory.CreateDbContextAsync(ct);
        try
        {
            var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('vairas.school_id',{school.ToString("D")},true), set_config('vairas.user_id',{user.ToString("D")},true)", ct);
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='5s'; SET CONSTRAINTS ALL DEFERRED", ct);
            if (write)
            {
                // Locks only local work by the same tenant/user; no HTTP/model work holds this transaction.
                var bytes = System.Security.Cryptography.SHA256.HashData(school.ToByteArray().Concat(user.ToByteArray()).ToArray());
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BinaryPrimitives.ReadInt64LittleEndian(bytes)})", ct);
            }
            else db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            return new Session(db, tx, school, options.DeploymentId);
        }
        catch { await db.DisposeAsync(); throw; }
    }
    public async Task<IReadOnlyList<AuditEntry>> AuditAsync(Guid school, Guid user, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await db.Database.OpenConnectionAsync(ct);
        await using var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand();
        command.CommandText = "SELECT operation_id,tool,outcome,recorded_at FROM chat_audit(@school,@limit)";
        command.Parameters.AddWithValue("school", school); command.Parameters.AddWithValue("limit", limit);
        var result = new List<AuditEntry>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
        return result;
    }
    public async Task<ConfirmationEvidence> ConfirmationAsync(ConfirmationQuery query, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await db.Database.OpenConnectionAsync(ct);
        await using var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand();
        command.CommandText = "SELECT confirmed_at,expires_at FROM chat_confirmation(@school,@conversation,@invocation,@identity,@operation,@digest)";
        command.Parameters.AddWithValue("school", query.SchoolId); command.Parameters.AddWithValue("conversation", query.SessionId); command.Parameters.AddWithValue("invocation", query.PreviewId);
        command.Parameters.AddWithValue("identity", query.IdentityId); command.Parameters.AddWithValue("operation", query.OperationId); command.Parameters.AddWithValue("digest", query.ArgumentsFingerprint);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(true, false, reader.GetFieldValue<DateTimeOffset>(0), reader.GetFieldValue<DateTimeOffset>(1)) : new(false);
    }
    public async Task<IReadOnlyList<ExecutionLocation>> PendingExecutionsAsync(DateTimeOffset before, int limit, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await db.Database.OpenConnectionAsync(ct);
        await using var command = ((NpgsqlConnection)db.Database.GetDbConnection()).CreateCommand();
        command.CommandText = "SELECT school_id,user_id,preview_id FROM chat_pending_executions(@before,@limit)";
        command.Parameters.AddWithValue("before", before); command.Parameters.AddWithValue("limit", limit);
        var result = new List<ExecutionLocation>(); await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)));
        return result;
    }
    private sealed class Session(ChatDbContext db, IDbContextTransaction tx, Guid school, Guid deployment) : IChatSession
    {
        public Task<T?> FindAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct) where T : class, ISchoolRow => db.Set<T>().Where(r => r.SchoolId == school).SingleOrDefaultAsync(predicate, ct);
        public async Task<IReadOnlyList<T>> ListAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken ct, int limit = 100, bool newestFirst = false) where T : class, ISchoolRow
        {
            if (limit is < 1 or > 1000) throw ChatErrors.Invalid();
            var query = db.Set<T>().Where(r => r.SchoolId == school).Where(predicate);
            if (typeof(T) == typeof(ChatMessage)) query = newestFirst ? query.OrderByDescending(r => EF.Property<long>(r, "SequenceNo")) : query.OrderBy(r => EF.Property<long>(r, "SequenceNo"));
            else query = newestFirst ? query.OrderByDescending(r => EF.Property<DateTimeOffset>(r, "CreatedAt")).ThenByDescending(r => EF.Property<Guid>(r, "Id")) : query.OrderBy(r => EF.Property<DateTimeOffset>(r, "CreatedAt")).ThenBy(r => EF.Property<Guid>(r, "Id"));
            return await query.Take(limit).ToArrayAsync(ct);
        }
        public Task<ModelDeployment?> ModelAsync(CancellationToken ct) => db.Set<ModelDeployment>().AsNoTracking().SingleOrDefaultAsync(m => m.Id == deployment && m.Enabled, ct);
        public void Add<T>(T row) where T : class, ISchoolRow { if (row.SchoolId != school) throw ChatErrors.Missing(); db.Add(row); }
        public async Task FlushAsync(CancellationToken ct)
        {
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: "23505" }) { throw ChatErrors.Conflict("IDEMPOTENCY_KEY_REUSED"); }
        }
        public async Task CommitAsync(CancellationToken ct) { await FlushAsync(ct); await tx.CommitAsync(ct); }
        public async ValueTask DisposeAsync() { await tx.DisposeAsync(); await db.DisposeAsync(); }
    }
}
