using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed record ModelTurn(string? Text, IReadOnlyList<ModelCall> Calls, long? InputTokens = null, long? OutputTokens = null);
