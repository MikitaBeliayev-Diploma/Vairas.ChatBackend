using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
namespace Vairas.ChatBackend;

public sealed class ChatDocumentTransformer(IConfiguration configuration) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken ct)
    {
        document.Info.Title = "Vairas Chat Backend API"; document.Info.Version = "1.0.0";
        document.Info.Description = "Private school-scoped conversations. PreviewOnlyV1 records consent only. Explicitly enabled ConfirmedLessonActionsV1 executes exact Administrator-approved practical booking and rescheduling with owner receipts and uncertainty recovery. Recorded-evidence queries always report executionAllowed=false; separate Scheduling-only authorization binds one command.";
        document.Components ??= new(); document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["bearerAuth"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" };
        document.Components.SecuritySchemes["workloadOAuth"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.OAuth2, Flows = new OpenApiOAuthFlows { ClientCredentials = new OpenApiOAuthFlow { TokenUrl = new Uri(new Uri(configuration["Dependencies:AuthBaseUrl"] ?? "https://localhost:7100/"), "connect/token"), Scopes = new Dictionary<string, string> { ["confirmations.validate"] = "Query recorded intent; no dispatch authorization", ["chat.lesson-actions.authorize"] = "Scheduling-only validation of an exact persisted execution or recovery intent" } } } };
        foreach (var (path, item) in document.Paths)
        foreach (var (method, operation) in item.Operations ?? [])
        {
            var internalRoute = path.StartsWith("/internal/", StringComparison.Ordinal);
            operation.Security = [new() { [new OpenApiSecuritySchemeReference(internalRoute ? "workloadOAuth" : "bearerAuth", document)] = internalRoute ? [path.Contains("lesson-authorizations", StringComparison.Ordinal) ? "chat.lesson-actions.authorize" : "confirmations.validate"] : [] }];
            operation.Responses ??= new();
            foreach (var status in new[] { "400", "401", "403", "404", "409", "422", "429", "503" }) operation.Responses.TryAdd(status, new OpenApiResponse { Description = status switch { "400" => "Invalid input", "401" => "Authentication required", "403" => "Permission denied", "404" => "Missing or concealed resource", "409" => "Revision, scope or idempotency conflict", "422" => "Owner rejected input", "429" => "Rate limit exceeded", _ => "Dependency or model unavailable" }, Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = new OpenApiSchema { Type = JsonSchemaType.Object } } } });
            if (path.EndsWith("/confirmations", StringComparison.Ordinal) || path.EndsWith("/executions", StringComparison.Ordinal))
            { operation.Parameters ??= []; operation.Parameters.Add(new OpenApiParameter { Name = "Origin", In = ParameterLocation.Header, Required = true, Schema = new OpenApiSchema { Type = JsonSchemaType.String }, Description = "Exact configured first-party HTTPS origin." }); }
        }
        return Task.CompletedTask;
    }
}
