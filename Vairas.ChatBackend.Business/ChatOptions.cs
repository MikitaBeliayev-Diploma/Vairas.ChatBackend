using System.Text.Json;
using System.Text.Json.Serialization;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public sealed class ChatOptions
{
    public int RunSeconds { get; set; } = 60;
    public int ModelSeconds { get; set; } = 20;
    public int PreviewSeconds { get; set; } = 300;
    public int MaxToolCalls { get; set; } = 4;
    public bool EmergencyDisabled { get; set; }
    public bool RecoveryWorkerEnabled { get; set; } = true;
    public bool ConfirmedLessonActionsEnabled { get; set; }
}
