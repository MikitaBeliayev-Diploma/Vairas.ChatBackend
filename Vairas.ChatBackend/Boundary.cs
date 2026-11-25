using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend;


public sealed partial class Boundary(RequestDelegate next, ILogger<Boundary> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        var supplied = http.Request.Headers["X-Correlation-ID"];
        var correlation = supplied.Count == 1 && Pattern().IsMatch(supplied[0]!) ? supplied[0]! : Guid.NewGuid().ToString("D");
        http.Items["CorrelationId"] = correlation; http.Response.Headers["X-Correlation-ID"] = correlation;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlation });
        var watch = Stopwatch.StartNew();
        try
        {
            if (!http.Request.IsHttps && !http.Request.Path.StartsWithSegments("/health") && (http.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(http.Connection.RemoteIpAddress))) { await Problems.WriteAsync(http, 403, "TLS_REQUIRED"); return; }
            await next(http);
            if (http.Response.StatusCode >= 400 && !http.Response.HasStarted && http.Response.ContentLength is null or 0 && string.IsNullOrEmpty(http.Response.ContentType)) await Problems.WriteAsync(http, http.Response.StatusCode, "HTTP_ERROR");
        }
        catch (BusinessException e) { await Problems.WriteAsync(http, StatusOf(e), e.Code); }
        catch (BadHttpRequestException e) { await Problems.WriteAsync(http, e.StatusCode, "VALIDATION_FAILED"); }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { http.Abort(); }
        catch (Exception e) { logger.LogError("Chat request failed with {ExceptionType}", e.GetType().Name); await Problems.WriteAsync(http, 500, "INTERNAL_ERROR"); }
        finally { logger.LogInformation("{Method} {Endpoint} returned {StatusCode} in {ElapsedMs} ms", http.Request.Method, http.GetEndpoint()?.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IEndpointNameMetadata>()?.EndpointName ?? "unmatched", http.Response.StatusCode, watch.ElapsedMilliseconds); }
    }
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex Pattern();

    private static int StatusOf(BusinessException exception) => exception switch
    {
        BadRequestException => StatusCodes.Status400BadRequest,
        UnauthorizedException => StatusCodes.Status401Unauthorized,
        ForbiddenException => StatusCodes.Status403Forbidden,
        NotFoundException => StatusCodes.Status404NotFound,
        ConflictException => StatusCodes.Status409Conflict,
        UnprocessableEntityException => StatusCodes.Status422UnprocessableEntity,
        ServiceUnavailableException => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError
    };
}
