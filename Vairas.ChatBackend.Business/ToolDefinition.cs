using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed record ToolDefinition(string Name, string Description, JsonElement Schema, bool Preview);
