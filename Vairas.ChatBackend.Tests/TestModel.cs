using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;

namespace Vairas.ChatBackend.Tests;

public sealed class TestModel : IModelAdapter
{
    public bool Enabled { get; set; } = true;
    public int Calls { get; private set; }
    public List<IReadOnlyList<ModelMessage>> Prompts { get; } = [];
    public Queue<ModelTurn> Turns { get; } = new();
    public Func<CancellationToken, Task>? BeforeResponse { get; set; }
    public async Task<ModelTurn> GenerateAsync(ModelDeployment model, IReadOnlyList<ModelMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        Calls++; Prompts.Add(messages.ToArray());
        if (BeforeResponse is not null) await BeforeResponse(ct);
        return Turns.Count > 0 ? Turns.Dequeue() : new("Synthetic assistant response; no business action executed.", [], 5, 6);
    }
}
