namespace Vairas.ChatBackend.Domain;

public enum RunStatus { Queued, Running, AwaitingConfirmation, Completed, Failed, Cancelled }
