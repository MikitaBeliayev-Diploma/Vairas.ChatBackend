using Microsoft.AspNetCore.Mvc;
using Vairas.ChatBackend.Business;
namespace Vairas.ChatBackend;

public static class Endpoints
{
    public static void MapChatEndpoints(this WebApplication app)
    {
        var chat = app.MapGroup("/api/v1/chat").RequireAuthorization("user").WithTags("chat");
        chat.MapPost("/sessions", async (CreateSessionRequest request, [FromHeader(Name = "Idempotency-Key")] Guid operation, HttpContext http, ChatService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, operation, ActorClaims.Actor(http), ct);
            return TypedResults.Created($"/api/v1/chat/sessions/{result.SessionId}?schoolId={result.SchoolId}", result);
        }).WithName("createChatSession");
        chat.MapGet("/sessions", async (Guid schoolId, DateTimeOffset? before, Guid? beforeId, int? limit, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(schoolId, before, limit ?? 50, ActorClaims.Actor(http), ct, beforeId))).WithName("listChatSessions");
        chat.MapGet("/sessions/{sessionId:guid}", async (Guid schoolId, Guid sessionId, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.GetAsync(schoolId, sessionId, ActorClaims.Actor(http), ct))).WithName("getChatSession");
        chat.MapGet("/sessions/{sessionId:guid}/messages", async (Guid schoolId, Guid sessionId, long? after, int? limit, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.HistoryAsync(schoolId, sessionId, after ?? 0, limit ?? 50, ActorClaims.Actor(http), ct))).WithName("getChatHistory");
        chat.MapPost("/sessions/{sessionId:guid}/messages", async (Guid schoolId, Guid sessionId, SendMessageRequest request, [FromHeader(Name = "Idempotency-Key")] Guid operation, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.MessageAsync(schoolId, sessionId, request, operation, ActorClaims.Actor(http), ct))).WithName("sendChatMessage").RequireRateLimiting("messages");
        chat.MapGet("/sessions/{sessionId:guid}/runs/{runId:guid}", async (Guid schoolId, Guid sessionId, Guid runId, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.RunAsync(schoolId, sessionId, runId, ActorClaims.Actor(http), ct))).WithName("getAssistantRun");
        chat.MapPost("/sessions/{sessionId:guid}/archive", async (Guid schoolId, Guid sessionId, ArchiveRequest request, [FromHeader(Name = "Idempotency-Key")] Guid operation, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.ArchiveAsync(schoolId, sessionId, request, operation, ActorClaims.Actor(http), ct))).WithName("archiveChatSession");
        chat.MapGet("/action-previews/{previewId:guid}", async (Guid schoolId, Guid previewId, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.PreviewAsync(schoolId, previewId, ActorClaims.Actor(http), ct))).WithName("getChatActionPreview");
        chat.MapPost("/action-previews/{previewId:guid}/confirmations", async (Guid schoolId, Guid previewId, ApprovalRequest request, [FromHeader(Name = "Idempotency-Key")] Guid operation, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.ApproveAsync(schoolId, previewId, request, operation, ActorClaims.Actor(http), ct))).WithName("recordExactChatApproval");
        chat.MapPost("/action-previews/{previewId:guid}/executions", async (Guid schoolId, Guid previewId, ApprovalRequest request, [FromHeader(Name = "Idempotency-Key")] Guid operation, HttpContext http, ChatService service, CancellationToken ct) =>
        {
            var receipt = await service.ExecuteAsync(schoolId, previewId, request, operation, ActorClaims.Actor(http), ct);
            return Results.Json(receipt, statusCode: receipt.HttpStatus);
        }).WithName("executeConfirmedChatLessonAction").Produces<Vairas.ConfirmedActions.LessonActionReceipt>(201).Produces<Vairas.ConfirmedActions.LessonActionReceipt>(200).Produces<Vairas.ConfirmedActions.LessonActionReceipt>(202).Produces<Vairas.ConfirmedActions.LessonActionReceipt>(409);
        chat.MapGet("/action-previews/{previewId:guid}/execution", async (Guid schoolId, Guid previewId, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.ExecutionAsync(schoolId, previewId, ActorClaims.Actor(http), ct))).WithName("getChatLessonExecution");
        app.MapPost("/internal/v1/chat/lesson-authorizations/validate", async (Vairas.ConfirmedActions.LessonAuthorizationQuery request, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.AuthorizeLessonAsync(request, ct))).RequireAuthorization("lesson.authorize").WithName("authorizeExactChatLessonAction");
        app.MapGet("/api/v1/schools/{schoolId:guid}/assistant-audit", async (Guid schoolId, int? limit, HttpContext http, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.AuditAsync(schoolId, limit ?? 50, ActorClaims.Actor(http), ct))).RequireAuthorization("user").WithName("getAssistantAudit").WithTags("chat");
        app.MapPost("/internal/v1/chat/confirmations/validate", async (ConfirmationQuery request, ChatService service, CancellationToken ct) => TypedResults.Ok(await service.ConfirmationAsync(request, ct))).RequireAuthorization("confirmation.read").WithName("queryRecordedChatConfirmation").WithTags("chat-internal");
    }
}
