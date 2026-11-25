using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed record OwnerPreview(string PreviewVersion, string Kind, JsonElement Arguments, JsonElement SourceRevisions, JsonElement Display);
