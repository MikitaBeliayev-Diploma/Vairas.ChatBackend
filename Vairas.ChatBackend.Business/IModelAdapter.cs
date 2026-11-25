using System.Linq.Expressions;
using System.Text.Json;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Business;

public interface IModelAdapter
{
    bool Enabled { get; }
    Task<ModelTurn> GenerateAsync(ModelDeployment model, IReadOnlyList<ModelMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct);
}
