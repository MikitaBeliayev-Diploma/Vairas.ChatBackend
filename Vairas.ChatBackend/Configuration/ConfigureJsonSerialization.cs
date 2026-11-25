using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;

namespace Vairas.ChatBackend.Configuration;

/// <summary>Strict JSON contract: no integer enums, no unknown members, no duplicate properties; bad requests surface as exceptions.</summary>
public static class ConfigureJsonSerialization
{
    public static IServiceCollection AddStrictJsonSerialization(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.AllowDuplicateProperties = false;
            options.SerializerOptions.PropertyNameCaseInsensitive = false;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        });
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        return services;
    }
}
