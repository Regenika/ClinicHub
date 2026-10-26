namespace ClinicHub.Api.Modules.Messaging.Scenarios;

/// <summary>
/// Фон сценариев: раз в минуту отправляет наступившие напоминания, раз в час ищет «давно не были».
/// Обе операции идемпотентны, поэтому частый запуск безопасен.
/// </summary>
public sealed class ReminderWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReminderWorker> logger) : BackgroundService
{
    private static readonly TimeSpan DispatchEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ScanEvery = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ReminderWorker запущен");
        var lastScan = DateTimeOffset.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (clock.GetUtcNow() - lastScan >= ScanEvery)
                {
                    await scope.ServiceProvider.GetRequiredService<ScenarioPlanner>().RunNoVisitScanAsync(stoppingToken);
                    lastScan = clock.GetUtcNow();
                }
                await scope.ServiceProvider.GetRequiredService<ReminderDispatcher>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка в сценариях напоминаний");
            }

            try
            {
                await Task.Delay(DispatchEvery, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
