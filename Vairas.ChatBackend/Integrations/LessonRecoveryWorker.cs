using System.Net.Http.Json;
using System.Text.Json;
using ModelContextProtocol.Client;
using Vairas.ChatBackend.Business;
using Vairas.ChatBackend.Domain;
using Vairas.ConfirmedActions;
using Vairas.ChatBackend.Configuration.Options;
namespace Vairas.ChatBackend.Integrations;

public sealed class LessonRecoveryWorker(IServiceScopeFactory scopes, ILogger<LessonRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { using var scope = scopes.CreateScope(); if (scope.ServiceProvider.GetRequiredService<IChatSettings>().Current.RecoveryWorkerEnabled) await scope.ServiceProvider.GetRequiredService<ChatService>().ReconcileExecutionsAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Lesson recovery scan unavailable: {ExceptionType}", e.GetType().Name); }
        }
    }
}
