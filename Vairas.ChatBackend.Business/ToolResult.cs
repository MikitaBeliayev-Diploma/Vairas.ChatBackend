using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed record ToolResult(JsonElement Value, OwnerPreview? Preview = null);
