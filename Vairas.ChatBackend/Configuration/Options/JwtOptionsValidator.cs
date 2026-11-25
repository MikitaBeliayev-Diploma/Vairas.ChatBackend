using Microsoft.Extensions.Options;

namespace Vairas.ChatBackend.Configuration.Options;

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options) =>
        HttpsOrigin.IsValid(options.Authority) && HttpsOrigin.IsValid(options.Issuer)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Configure HTTPS authority, issuer and dependency origins.");
}
