using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ConfirmedActions;
using Xunit;
namespace Vairas.ChatBackend.Tests;

public sealed class LessonDispatcherFixture : ILessonActionDispatcher
{
    public int Dispatches, Recoveries;
    public bool LoseResponse, ThrowBeforeCommit;
    public LessonAction? Action;
    public Func<LessonAction, Task>? Check;
    public LessonActionReceipt? Receipt;
    public async Task<LessonActionReceipt> ExecuteAsync(LessonAction action, ChatActor actor, CancellationToken ct)
    {
        Dispatches++; Action = action; if (Check is not null) await Check(action);
        if (ThrowBeforeCommit) throw new HttpRequestException();
        Receipt = new(action.Binding.OperationId, "Committed", Guid.NewGuid(), 1, "COMMITTED", action.Binding.Kind == "Booking" ? 201 : 200);
        if (LoseResponse) throw new HttpRequestException(); return Receipt;
    }
    public Task<LessonActionReceipt> ResolveAsync(LessonAction action, CancellationToken ct)
    { Recoveries++; return Task.FromResult(Receipt ?? new(action.Binding.OperationId, "Rejected", Code: "NOT_EXECUTED", HttpStatus: 409)); }
}
