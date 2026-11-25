using Microsoft.Extensions.Options;
using Vairas.ChatBackend.Business;

namespace Vairas.ChatBackend.Configuration.Options;

public sealed class ChatOptionsValidator : IValidateOptions<ChatOptions>
{
    public ValidateOptionsResult Validate(string? name, ChatOptions o) =>
        o.RunSeconds is >= 1 and <= 60 && o.ModelSeconds is >= 1 and <= 20 && o.PreviewSeconds is >= 1 and <= 300 && o.MaxToolCalls is >= 1 and <= 4
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Invalid bounded Chat limits.");
}
