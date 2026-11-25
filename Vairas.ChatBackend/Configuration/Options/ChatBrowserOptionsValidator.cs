using Microsoft.Extensions.Options;

namespace Vairas.ChatBackend.Configuration.Options;

public sealed class ChatBrowserOptionsValidator : IValidateOptions<ChatBrowserOptions>
{
    public ValidateOptionsResult Validate(string? name, ChatBrowserOptions options) =>
        options.BrowserOrigins.Length == 0 || options.BrowserOrigins.Any(origin => !HttpsOrigin.IsValid(origin) || new Uri(origin).GetLeftPart(UriPartial.Authority) != origin)
            ? ValidateOptionsResult.Fail("Configure exact HTTPS confirmation origins.")
            : ValidateOptionsResult.Success;
}
