using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Bounded final response to an idempotent user message.</summary>
public sealed record ChatReply(Guid MessageId, string Text, Guid? PreviewId, Guid RunId);
