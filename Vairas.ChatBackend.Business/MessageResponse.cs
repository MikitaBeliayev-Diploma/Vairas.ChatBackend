using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>One visible history item without hidden model reasoning.</summary>
public sealed record MessageResponse(Guid MessageId, long SequenceNo, string Role, string Text, DateTimeOffset CreatedAt);
