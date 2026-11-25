using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend.Integrations;

/// <summary>Bound SDK discovery/tool envelopes before JSON deserialization.</summary>
public sealed class McpResponseLimit : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        try
        {
            if (response.Content.Headers.ContentLength > 524288) throw ChatErrors.Dependency("MCP_RESPONSE_TOO_LARGE");
            await using var stream = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream(); var chunk = new byte[8192];
            for (var length = await stream.ReadAsync(chunk, ct); length != 0; length = await stream.ReadAsync(chunk, ct))
            {
                if (buffer.Length + length > 524288) throw ChatErrors.Dependency("MCP_RESPONSE_TOO_LARGE");
                await buffer.WriteAsync(chunk.AsMemory(0, length), ct);
            }
            var replacement = new ByteArrayContent(buffer.ToArray());
            foreach (var header in response.Content.Headers) if (header.Key != "Content-Length") replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose(); response.Content = replacement; return response;
        }
        catch { response.Dispose(); throw; }
    }
}
