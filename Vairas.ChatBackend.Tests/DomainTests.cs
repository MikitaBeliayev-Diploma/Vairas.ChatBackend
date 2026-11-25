using System.Text.Json;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Xunit;
namespace Vairas.ChatBackend.Tests;

[Trait("Category", "Unit")]
public sealed class DomainTests
{
    [Theory]
    [InlineData("{\"b\":2,\"a\":1}", "{\"a\":1,\"b\":2}")]
    [InlineData("[null,true,false,0,-2]", "[null,true,false,0,-2]")]
    [InlineData("{\"text\":\"Литва🚗\"}", "{\"text\":\"Литва🚗\"}")]
    [InlineData("\"\\b\\t\\n\\f\\r\\\"\\\\\\u0001\"", "\"\\b\\t\\n\\f\\r\\\"\\\\\\u0001\"")]
    public void Canonical_JSON_is_sorted_UTF8_and_preserves_array_order(string json, string canonical)
    {
        using var parsed = JsonDocument.Parse(json); Assert.Equal(canonical, ChatRules.Canonical(parsed.RootElement));
        Assert.Equal(64, ChatRules.Hash(parsed.RootElement).Length);
    }
    [Theory]
    [InlineData("{\"a\":1,\"a\":2}")][InlineData("1.25")][InlineData("9007199254740992")][InlineData("-9007199254740992")]
    public void Ambiguous_or_unsafe_canonical_arguments_are_rejected(string json)
    {
        using var parsed = JsonDocument.Parse(json); Business.ThrowsBusiness(() => ChatRules.Canonical(parsed.RootElement));
    }
    [Fact]
    public void Message_limits_unicode_and_empty_keys_are_enforced()
    {
        ChatRules.Text(new string('x', 16384));
        foreach (var text in new[] { "", "  ", new string('x', 16385), new string('Ж', 9000), "\uD800", "\uDC00", "\uD800x" }) Business.ThrowsBusiness(() => ChatRules.Text(text));
        Business.ThrowsBusiness(() => ChatRules.Id(Guid.Empty));
        var now = DateTimeOffset.UtcNow; Business.ThrowsBusiness(() => ChatRules.Fresh(now, now));
        Assert.Equal(0, ChatRules.Instant(now).Ticks % 10);
    }
    [Theory]
    [InlineData("{\"url\":\"https://evil.invalid\"}")]
    [InlineData("{\"actorUserId\":\"11111111-1111-4111-8111-111111111111\"}")]
    [InlineData("[]")]
    [InlineData("{\"enrollmentId\":null}")]
    [InlineData("{\"enrollmentId\":\"bad\"}")]
    [InlineData("{}")]
    public void Tool_schemas_are_closed_and_require_valid_typed_arguments(string json)
    {
        using var value = JsonDocument.Parse(json);
        Business.ThrowsBusiness(() => ToolCatalog.Normalize("get_student_training_progress", value.RootElement, Guid.NewGuid()));
    }
    [Fact]
    public void School_overrides_and_invalid_ranges_are_rejected()
    {
        var w = new TestWorld(); var booking = System.Text.Json.Nodes.JsonNode.Parse(w.Booking().GetRawText())!.AsObject(); booking["schoolId"] = Guid.NewGuid();
        Business.ThrowsBusiness(() => ToolCatalog.Normalize("preview_lesson_booking", Wire.Element(booking), w.School));
        Business.ThrowsBusiness(() => ToolCatalog.Normalize("get_my_lessons", Wire.Element(new { from = "bad", to = "bad" }), w.School));
        Business.ThrowsBusiness(() => ToolCatalog.Normalize("get_my_lessons", Wire.Element(new { from = "2030-01-01T00:00:00Z", to = "2031-01-01T00:00:00Z" }), w.School));
        booking = System.Text.Json.Nodes.JsonNode.Parse(w.Booking().GetRawText())!.AsObject(); booking["endsAt"] = booking["startsAt"]!.DeepClone();
        Business.ThrowsBusiness(() => ToolCatalog.Normalize("preview_lesson_booking", Wire.Element(booking), w.School));
        Assert.Equal(6, ToolCatalog.All.Count);
        Assert.DoesNotContain(ToolCatalog.All, t => t.Name.StartsWith("create_", StringComparison.Ordinal) || t.Name.StartsWith("confirm", StringComparison.Ordinal));
    }
    [Fact]
    public void Integer_ranges_and_rescheduling_reason_are_checked()
    {
        var school = Guid.NewGuid();
        var args = Wire.Element(new { lessonId = Guid.NewGuid(), enrollmentId = Guid.NewGuid(), expectedVersion = 0, startsAt = "2030-01-01T10:00:00Z", endsAt = "2030-01-01T11:00:00Z", reason = "Move" });
        Business.ThrowsBusiness(() => ToolCatalog.Normalize("preview_lesson_rescheduling", args, school));
        var node = System.Text.Json.Nodes.JsonNode.Parse(args.GetRawText())!.AsObject(); node["expectedVersion"] = 1; node["reason"] = new string('x', 501);
        Business.ThrowsBusiness(() => ToolCatalog.Normalize("preview_lesson_rescheduling", Wire.Element(node), school));
        node["reason"] = "Move"; node["expectedVersion"] = "one";
        Business.ThrowsBusiness(() => ToolCatalog.Normalize("preview_lesson_rescheduling", Wire.Element(node), school));
    }
}
