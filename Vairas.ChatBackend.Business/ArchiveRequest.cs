using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Compare-and-swap metadata lifecycle command.</summary>
public sealed record ArchiveRequest(long ExpectedRevision);
