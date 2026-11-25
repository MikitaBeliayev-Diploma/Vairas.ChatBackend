using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Create a conversation under one current authorized school.</summary>
public sealed record CreateSessionRequest(Guid SchoolId, string ExecutionProfile = "PreviewOnlyV1");
