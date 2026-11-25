using Microsoft.EntityFrameworkCore;
using Vairas.ChatBackend.Domain;
using Vairas.ChatBackend.Persistance;
namespace Vairas.ChatBackend.Startup;

/// <summary>Explicit, offline provisioning entry point. Never invoked by the HTTP host.</summary>
public static class ChatProvisioning
{
    public static async Task InitializeAsync(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("VAIRAS_CHAT_MIGRATION_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Set VAIRAS_CHAT_MIGRATION_CONNECTION to the dedicated migration owner.");
        await using var db = new ChatDesignFactory().CreateDbContext([]);
        await db.Database.OpenConnectionAsync();
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT current_user=pg_get_userbyid(datdba) AND NOT rolsuper AND NOT rolbypassrls FROM pg_database,pg_roles WHERE datname=current_database() AND rolname=current_user";
            if (await command.ExecuteScalarAsync() is not true) throw new InvalidOperationException("Use a dedicated non-superuser database owner, never the runtime role.");
        }
        await db.Database.MigrateAsync();
        await db.Database.CloseConnectionAsync();
        await using var provision = new ChatDesignFactory().CreateDbContext([]);
        await provision.Database.OpenConnectionAsync();
        await ((Npgsql.NpgsqlConnection)provision.Database.GetDbConnection()).ReloadTypesAsync();
        var grants = Path.Combine(AppContext.BaseDirectory, "database", "runtime-grants.sql");
        await provision.Database.ExecuteSqlRawAsync(await File.ReadAllTextAsync(grants));
        if (args.Contains("--register-model", StringComparer.Ordinal))
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var id = Guid.Parse(configuration["Store:DeploymentId"] ?? throw new InvalidOperationException("Set Store__DeploymentId."));
            if (id == Guid.Empty) throw new InvalidOperationException("DeploymentId must be nonempty.");
            string Required(string key, int max)
            {
                var value = configuration[key];
                if (string.IsNullOrWhiteSpace(value) || value.Length > max) throw new InvalidOperationException($"Configure {key}.");
                return value;
            }
            var deployment = Required("Model:Deployment", 160); var region = Required("Model:Region", 80); var model = Required("Model:Name", 160);
            var existing = await provision.Set<ModelDeployment>().SingleOrDefaultAsync(m => m.Id == id);
            if (existing is null)
            {
                provision.Add(new ModelDeployment { Id = id, Provider = "AzureOpenAI", ModelName = model, ModelVersion = configuration["Model:Version"], DeploymentName = deployment, Region = region, EndpointConfigKey = "Model:Endpoint", CredentialSecretRef = "Model:Credential", ConfigurationVersion = 1, Enabled = false, CreatedAt = DateTimeOffset.UtcNow });
                await provision.SaveChangesAsync();
            }
            else if (existing.Provider != "AzureOpenAI" || existing.ModelName != model || existing.ModelVersion != configuration["Model:Version"] || existing.DeploymentName != deployment || existing.Region != region)
                throw new InvalidOperationException("Deployment identity is immutable; register a new DeploymentId for changed configuration.");
        }
        Console.WriteLine("Chat schema and restricted grants initialized. Registered deployments start disabled; no model was called.");
    }
}
