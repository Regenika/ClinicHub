namespace ClinicHub.Api.Modules.Visits;

/// <summary>Событие «визит завершён» (подтверждён как состоявшийся).</summary>
public sealed record VisitCompleted(
    int VisitId, int PatientId, DateTime StartAtUtc, IReadOnlyList<string> ServiceIds, int? ConfirmedById);

/// <summary>
/// Подписчик на «визит завершён». Сюда подключится планировщик сценариев напоминаний (#14, ADR-0008 §4):
/// достаточно зарегистрировать реализацию в DI — <c>services.AddScoped&lt;IVisitCompletedHandler, ...&gt;()</c>.
/// </summary>
public interface IVisitCompletedHandler
{
    Task HandleAsync(VisitCompleted e, CancellationToken ct);
}

/// <summary>
/// Простая внутренняя публикация без шины: вызывает всех зарегистрированных подписчиков по очереди
/// после сохранения визита. Ошибка подписчика не отменяет подтверждение визита — она пишется в журнал.
/// </summary>
public sealed class VisitEvents(IEnumerable<IVisitCompletedHandler> handlers, ILogger<VisitEvents> logger)
{
    public async Task PublishAsync(VisitCompleted e, CancellationToken ct)
    {
        var list = handlers.ToList();
        logger.LogInformation("Визит #{VisitId} завершён, подписчиков: {Count}", e.VisitId, list.Count);
        foreach (var h in list)
        {
            try
            {
                await h.HandleAsync(e, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Подписчик {Handler} упал на визите #{VisitId}", h.GetType().Name, e.VisitId);
            }
        }
    }
}
