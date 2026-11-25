using System.Text.Json;
using Vairas.ChatBackend.Domain;
using Vairas.ConfirmedActions;
namespace Vairas.ChatBackend.Business;

public interface ILessonActionDispatcher
{
    Task<LessonActionReceipt> ExecuteAsync(LessonAction action, ChatActor actor, CancellationToken ct);
    Task<LessonActionReceipt> ResolveAsync(LessonAction action, CancellationToken ct);
}
