using Microsoft.Extensions.Options;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Configuration.Options;
using Vairas.ChatBackend.Persistance;

namespace Vairas.ChatBackend.Configuration;

/// <summary>Binds and validates every strongly typed options section; invalid required settings fail at startup.</summary>
public static class ConfigureOptions
{
    public static IServiceCollection AddChatOptions(this IServiceCollection services, IConfiguration configuration)
    {
        AddValidated<DatabaseOptions, DatabaseOptionsValidator>(services, configuration.GetSection("ConnectionStrings"));
        AddValidated<JwtOptions, JwtOptionsValidator>(services, configuration.GetSection("Authentication"));
        AddValidated<DependencyOptions, DependencyOptionsValidator>(services, configuration.GetSection("Dependencies"));
        AddValidated<ModelOptions, ModelOptionsValidator>(services, configuration.GetSection("Model"));
        AddValidated<ChatBrowserOptions, ChatBrowserOptionsValidator>(services, configuration.GetSection("Chat"));
        AddValidated<ChatOptions, ChatOptionsValidator>(services, configuration.GetSection("Chat"));
        services.AddOptions<McpOptions>().Bind(configuration.GetSection("Mcp"));
        services.AddOptions<StoreOptions>().Bind(configuration.GetSection("Store"));
        // Infrastructure adapters take the plain settings objects; hosts and tests may replace them.
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<DependencyOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<ModelOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<McpOptions>>().Value);
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<StoreOptions>>().Value);
        services.AddSingleton<IChatSettings, RuntimeChatSettings>();
        return services;
    }

    private static void AddValidated<TOptions, TValidator>(IServiceCollection services, IConfigurationSection section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>().Bind(section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TOptions>, TValidator>();
    }
}
