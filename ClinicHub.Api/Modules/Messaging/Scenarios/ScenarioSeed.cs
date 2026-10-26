using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Messaging.Scenarios;

/// <summary>
/// Примеры сценариев из ADR-0008 §3 — создаются <b>выключенными</b>, чтобы ничего не ушло без решения клиники.
/// Тексты без диагноза и названия процедуры (врачебная тайна, ADR-0008 §5). Деление на Service / Marketing
/// утверждает клиника с юристом.
/// </summary>
public static class ScenarioSeed
{
    public static async Task SeedAsync(ClinicHubDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (await db.CareScenarios.AnyAsync(ct))
            return;

        var injections = await db.Services.AnyAsync(s => s.Id == "injections", ct);

        var control = new CareScenario
        {
            Name = "Контрольный осмотр через 14 дней",
            IsActive = false,
            Purpose = ScenarioPurpose.Service,
            Trigger = ScenarioTrigger.VisitCompleted,
            Steps =
            {
                new CareScenarioStep
                {
                    Order = 1, OffsetDays = 14, SendTime = new TimeSpan(11, 0, 0), CancelIfFutureVisit = true,
                    Text = "{name}, здравствуйте! Пора на контрольный осмотр в клинику «Регеника». Записаться: +7 911 210-00-50",
                },
            },
        };
        if (injections)
            control.TriggerServices.Add(new CareScenarioService { ServiceId = "injections" });

        var longAgo = new CareScenario
        {
            Name = "Давно не были (180 дней)",
            IsActive = false,
            Purpose = ScenarioPurpose.Marketing,
            Trigger = ScenarioTrigger.NoVisitFor,
            NoVisitDays = 180,
            Steps =
            {
                new CareScenarioStep
                {
                    Order = 1, OffsetDays = 0, SendTime = new TimeSpan(12, 0, 0), CancelIfFutureVisit = true, CancelIfVisitAfter = true,
                    Text = "{name}, здравствуйте! Давно вас не видели в «Регенике». Будем рады помочь: +7 911 210-00-50",
                },
            },
        };

        db.CareScenarios.AddRange(control, longAgo);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Созданы примеры сценариев напоминаний (выключены): {Count}", 2);
    }
}
