using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

/// <summary>Consent evidence does not grant business dispatch.</summary>
public sealed record ConfirmationEvidence(bool Confirmed, bool ExecutionAllowed = false, DateTimeOffset? ConfirmedAt = null, DateTimeOffset? ExpiresAt = null);
