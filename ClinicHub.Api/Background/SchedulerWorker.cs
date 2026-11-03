using ClinicHub.Api.Services;

namespace ClinicHub.Api.Background;

/// <summary>Фоновый планировщик: превращает заявки в очередь и распределяет по SIM с лимитами.</summary>
public class SchedulerWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<SchedulerWorker> _logger;

    public SchedulerWorker(IServiceProvider services, ILogger<SchedulerWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SchedulerWorker запущен");
        // Пока база не готова (#48), работать не с чем.
        if (!await _services.GetRequiredService<Data.DatabaseReadiness>().WaitAsync(stoppingToken))
            return;


        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var scheduler = _services.CreateScope().ServiceProvider
                    .GetRequiredService<SchedulerService>();

                await scheduler.PlanAsync(stoppingToken);
                await scheduler.RequeueStaleAsync(stoppingToken);
                var assigned = await scheduler.DistributeAsync(stoppingToken);
                if (assigned > 0)
                    _logger.LogInformation("Распределено задач: {N}", assigned);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка планировщика");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}