using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Integrations;


public interface IWorkloadTokens { Task<string> GetAsync(CancellationToken ct); }
