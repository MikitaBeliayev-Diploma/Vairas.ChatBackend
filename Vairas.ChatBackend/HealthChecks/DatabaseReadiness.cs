using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Vairas.ChatBackend.Persistance;

namespace Vairas.ChatBackend.HealthChecks;

public sealed class DatabaseReadiness(IDbContextFactory<ChatDbContext> factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct); await db.Database.OpenConnectionAsync(ct);
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT NOT rolsuper AND NOT rolbypassrls AND NOT has_schema_privilege(current_user,'public','CREATE') AND NOT has_database_privilege(current_user,current_database(),'CREATE') AND NOT has_database_privilege(current_user,current_database(),'TEMPORARY') AND current_user<>pg_get_userbyid((SELECT datdba FROM pg_database WHERE datname=current_database())) FROM pg_roles WHERE rolname=current_user";
            if (await command.ExecuteScalarAsync(ct) is not true) return HealthCheckResult.Unhealthy("Restricted runtime role required.");
            command.CommandText = "SELECT creation_request_id,access_stamp,execution_profile FROM conversation LIMIT 0; SELECT execution_profile,deadline_at FROM assistant_run LIMIT 0; SELECT confirmed_digest FROM action_confirmation LIMIT 0; SELECT recovery_after FROM tool_invocation LIMIT 0;";
            await command.ExecuteNonQueryAsync(ct); return HealthCheckResult.Healthy();
        }
        catch (Exception) { return HealthCheckResult.Unhealthy("Runtime database prerequisites unavailable."); }
    }
}
