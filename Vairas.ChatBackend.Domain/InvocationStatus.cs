namespace Vairas.ChatBackend.Domain;

public enum InvocationStatus { Prepared, AwaitingConfirmation, Confirmed, Executing, Succeeded, Rejected, Failed, Unknown, Expired, Cancelled }
