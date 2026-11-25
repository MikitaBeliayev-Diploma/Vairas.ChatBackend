using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed record ModelMessage(string Role, string? Text = null, IReadOnlyList<ModelCall>? Calls = null, string? CallId = null);
