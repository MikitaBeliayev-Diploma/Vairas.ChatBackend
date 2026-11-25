using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Integrations;

namespace Vairas.ChatBackend.Configuration;

/// <summary>Application services; infrastructure registrations live in the dedicated Configure* classes.</summary>
public static class ConfigureDependencyInjection
{
    public static IServiceCollection AddChatServices(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ChatService>();
        services.AddScoped<IMcpTools, McpTools>();
        services.AddScoped<ILessonActionDispatcher, LessonActionDispatcher>();
        services.AddHostedService<LessonRecoveryWorker>();
        services.AddScoped<IChatOwners, ChatOwners>();
        services.AddSingleton<IModelAdapter, AzureModelAdapter>();
        return services;
    }
}
