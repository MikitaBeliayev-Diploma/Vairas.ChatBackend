using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Json) ?? throw ChatErrors.Invalid();
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw ChatErrors.Invalid();
}
