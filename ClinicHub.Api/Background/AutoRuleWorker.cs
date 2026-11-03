using ClinicHub.Api.Services;

namespace ClinicHub.Api.Background;

/// <summary>Периодически проверяет авто-правила (дни рождения и т.п.) и создаёт персональные заявки.</summary>
public class AutoRuleWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AutoRuleWorker> _logger;

    public AutoRuleWorker(IServiceProvider services, ILogger<AutoRuleWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoRuleWorker запущен");
        // Пока база не готова (#48), работать не с чем.
        if (!await _services.GetRequiredService<Data.DatabaseReadiness>().WaitAsync(stoppingToken))
            return;


        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var service = _services.CreateScope().ServiceProvider
                    .GetRequiredService<AutoRuleService>();
                await service.RunAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка проверки авто-правил");
            }

            try
            {
                // Идемпотентность через AutoRuleFire позволяет проверять часто, не боясь дублей.
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
