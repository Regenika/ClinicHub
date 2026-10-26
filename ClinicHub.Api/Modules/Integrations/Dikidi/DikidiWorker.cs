namespace ClinicHub.Api.Modules.Integrations.Dikidi;

/// <summary>
/// Фоновая синхронизация журнала Dikidi раз в <see cref="DikidiOptions.IntervalMinutes"/>.
/// Изолирована: любая её ошибка остаётся здесь (журнал + /api/debug/state), остальной сервер работает,
/// а визиты в это время можно вести вручную.
/// </summary>
public sealed class DikidiWorker(
    IServiceScopeFactory scopes, DikidiOptions options, DikidiSyncState state, ILogger<DikidiWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        state.Configured = options.IsConfigured;
        if (!options.IsConfigured)
        {
            logger.LogInformation(
                "Dikidi: адаптер выключен — не заданы Dikidi:Phone, Dikidi:Password, Dikidi:CompanyId (user-secrets или переменные окружения).");
            return;
        }

        logger.LogInformation("Dikidi: синхронизация журнала раз в {Minutes} мин, период −{Back}…+{Ahead} дн.",
            (int)options.Interval.TotalMinutes, options.DaysBack, options.DaysAhead);

        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<DikidiSync>().RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Текст уже без данных пациентов (см. DikidiSync); стек не пишем — в нём могут быть значения.
                logger.LogError("ОШИБКА синхронизации Dikidi: {Error}", state.LastError ?? ex.Message);
            }

            try { await Task.Delay(options.Interval, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
