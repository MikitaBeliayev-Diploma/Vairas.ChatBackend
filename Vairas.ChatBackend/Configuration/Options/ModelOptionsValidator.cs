using Microsoft.Extensions.Options;

namespace Vairas.ChatBackend.Configuration.Options;

/// <summary>A disabled model needs no endpoint; real personal data is never allowed in this stage.</summary>
public sealed class ModelOptionsValidator : IValidateOptions<ModelOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelOptions model) =>
        model.RealPersonalDataEnabled
        || model.Enabled && (!model.AllowSyntheticCloud || !HttpsOrigin.IsValid(model.Endpoint) || string.IsNullOrWhiteSpace(model.Deployment) || string.IsNullOrWhiteSpace(model.Region))
            ? ValidateOptionsResult.Fail("This release supports explicitly configured synthetic-only cloud generation.")
            : ValidateOptionsResult.Success;
}
