using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Vairas.ChatBackend.Configuration.Options;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Persistance;

namespace Vairas.ChatBackend.Configuration;

public static class ConfigureDatabase
{
    public static IServiceCollection AddChatDatabase(this IServiceCollection services)
    {
        services.AddDbContextFactory<ChatDbContext>((provider, builder) =>
            builder.UseNpgsql(provider.GetRequiredService<IOptions<DatabaseOptions>>().Value.Chat, ChatDbContext.Enums));
        services.AddScoped<IChatStore, ChatStore>();
        return services;
    }
}
