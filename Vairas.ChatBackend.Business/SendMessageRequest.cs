using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>User text only; an optional school must match the session.</summary>
public sealed record SendMessageRequest(string Text, Guid? SchoolId = null);
