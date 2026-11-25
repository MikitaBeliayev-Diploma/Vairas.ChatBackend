using Microsoft.Extensions.Options;
using Vairas.ChatBackend.Business;

namespace Vairas.ChatBackend.Configuration;

/// <summary>Chat limits read per use, so an operator change of the "Chat" section takes effect without restart.</summary>
public sealed class RuntimeChatSettings(IOptionsMonitor<ChatOptions> options) : IChatSettings
{
    public ChatOptions Current => options.CurrentValue;
}
