using Microsoft.Extensions.Options;

namespace Vairas.ChatBackend.Configuration.Options;

/// <summary>Skipped in the Testing environment, where tests supply disposable database roles.</summary>
public sealed class DatabaseOptionsValidator(IHostEnvironment environment) : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options) =>
        !environment.IsEnvironment("Testing") && string.IsNullOrWhiteSpace(options.Chat)
            ? ValidateOptionsResult.Fail("Configure restricted runtime database and workload credentials.")
            : ValidateOptionsResult.Success;
}
