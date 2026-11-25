using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Vairas.ChatBackend.Domain;
namespace Vairas.ChatBackend;


public static class Problems
{
    public static Task WriteAsync(HttpContext http, int status, string code)
    {
        if (http.Response.HasStarted) { http.Abort(); return Task.CompletedTask; }
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Title = "The chat request could not be completed.", Type = "about:blank", Extensions = { ["code"] = code, ["correlationId"] = http.Items["CorrelationId"], ["traceId"] = Activity.Current?.TraceId.ToString() ?? http.TraceIdentifier } }, options: null, contentType: "application/problem+json", cancellationToken: http.RequestAborted);
    }
}
