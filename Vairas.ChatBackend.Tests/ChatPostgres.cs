using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ChatBackend.Persistance;
using Xunit;
namespace Vairas.ChatBackend.Tests;

public sealed class ChatPostgres : IAsyncLifetime
{
    private readonly string secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private PostgreSqlContainer container = null!;
    public string Owner { get; private set; } = "";
    public string Runtime { get; private set; } = "";
    public string Operator { get; private set; } = "";
    public Guid Deployment { get; } = Guid.NewGuid();
    public async Task InitializeAsync()
    {
        container = new PostgreSqlBuilder("postgres:17").WithPassword(secret).Build(); await container.StartAsync();
        var admin = container.GetConnectionString();
        await Sql(admin, $"CREATE ROLE vairas_chat_migration LOGIN PASSWORD '{secret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; CREATE ROLE vairas_chat_runtime LOGIN PASSWORD '{secret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS; CREATE ROLE vairas_chat_operator LOGIN PASSWORD '{secret}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;");
        await Sql(admin, "CREATE DATABASE vairas_chat_test OWNER vairas_chat_migration");
        string Connection(string role) => new NpgsqlConnectionStringBuilder(admin) { Database = "vairas_chat_test", Username = role, Password = secret }.ConnectionString;
        Owner = Connection("vairas_chat_migration"); Runtime = Connection("vairas_chat_runtime"); Operator = Connection("vairas_chat_operator");
        await using (var db = Context(Owner)) { await db.Database.MigrateAsync(); await db.Database.MigrateAsync(); }
        await Sql(Owner, await File.ReadAllTextAsync(Path.Combine(Root, "database/runtime-grants.sql")));
        await using (var db = Context(Owner))
        {
            db.Add(new ModelDeployment { Id = Deployment, Provider = "Fixture", ModelName = "Synthetic", DeploymentName = "Synthetic", Region = "local", EndpointConfigKey = "Model:Endpoint", CredentialSecretRef = "Model:Credential", ConfigurationVersion = 1, Enabled = true, CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        }
    }
    public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
    public static ChatDbContext Context(string connection) => new(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(connection, ChatDbContext.Enums).Options);
    public ChatStore Store => new(new Factory(Runtime), new() { DeploymentId = Deployment });
    public ChatService Service(TestWorld w) => new(Store, w.Owners, w.Model, w.Settings, w.Clock);
    public static async Task Sql(string connection, string sql)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync(); }
    public static async Task<T> Scalar<T>(string connection, string sql)
    { await using var db = new NpgsqlConnection(connection); await db.OpenAsync(); await using var command = new NpgsqlCommand(sql, db); return (T)(await command.ExecuteScalarAsync())!; }
    public async Task DisposeAsync() { NpgsqlConnection.ClearAllPools(); if (container is not null) await container.DisposeAsync(); }
    public sealed class Factory(string connection) : IDbContextFactory<ChatDbContext>
    {
        public ChatDbContext CreateDbContext() => Context(connection);
        public Task<ChatDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
