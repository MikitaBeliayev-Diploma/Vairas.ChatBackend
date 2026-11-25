using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Npgsql.NameTranslation;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Persistance;

public sealed class ChatDesignFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    public ChatDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ChatDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("VAIRAS_CHAT_MIGRATION_CONNECTION") ?? "Host=localhost;Database=vairas_chat_design;Username=design", ChatDbContext.Enums).Options);
}
