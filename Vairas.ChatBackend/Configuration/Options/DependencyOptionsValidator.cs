using Microsoft.Extensions.Options;

namespace Vairas.ChatBackend.Configuration.Options;

public sealed class DependencyOptionsValidator(IHostEnvironment environment) : IValidateOptions<DependencyOptions>
{
    public ValidateOptionsResult Validate(string? name, DependencyOptions options)
    {
        if (!HttpsOrigin.IsValid(options.AuthBaseUrl) || !HttpsOrigin.IsValid(options.SchoolBaseUrl) || !HttpsOrigin.IsValid(options.BusinessApiBaseUrl))
            return ValidateOptionsResult.Fail("Configure HTTPS authority, issuer and dependency origins.");
        if (!environment.IsEnvironment("Testing") && string.IsNullOrWhiteSpace(options.ClientSecret))
            return ValidateOptionsResult.Fail("Configure restricted runtime database and workload credentials.");
        return ValidateOptionsResult.Success;
    }
}
