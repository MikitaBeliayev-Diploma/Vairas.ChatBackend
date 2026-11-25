using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Integrations;


public interface IMcpTools { Task<JsonElement> CallAsync(string name, JsonElement arguments, Guid school, ChatActor actor, CancellationToken ct); }
