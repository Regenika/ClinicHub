using ClinicHub.Api.Data;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Services;

/// <summary>
/// Проверяет активные авто-правила (день рождения и т.п.) и создаёт для подходящих
/// пациентов персональные заявки (RequestType.Personal), уже одобренные — они сразу
/// попадают в общий планировщик (SchedulerService) и рассылаются по тем же правилам
/// антиспама, что и обычные заявки.
/// </summary>
public class AutoRuleService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoRuleService> _logger;

    public AutoRuleService(IServiceScopeFactory scopeFactory, ILogger<AutoRuleService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHubDbContext>();

        var rules = await db.AutoRules.Where(r => r.IsActive).ToListAsync(ct);
        if (rules.Count == 0)
            return;

        var nowLocal = DateTime.Now;
        var todayLocal = nowLocal.Date;

        foreach (var rule in rules)
        {
            // Не раньше настроенного времени суток — чтобы не будить пациентов ночными поздравлениями.
            if (nowLocal.TimeOfDay < rule.SendTime)
                continue;

            switch (rule.Trigger)
            {
                case AutoRuleTrigger.Birthday:
                    await ProcessBirthdayAsync(db, rule, todayLocal, ct);
                    break;
            }

            rule.LastRunAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task ProcessBirthdayAsync(
        ClinicHubDbContext db, AutoRule rule, DateTime todayLocal, CancellationToken ct)
    {
        var targetDate = todayLocal.AddDays(rule.DaysOffset);

        var candidates = await db.Patients
            .Where(p => p.IsSubscribed && p.DateOfBirth != null && p.Phone != "")
            .ToListAsync(ct);

        var birthdayPatients = candidates
            .Where(p => p.DateOfBirth!.Value.Month == targetDate.Month
                     && p.DateOfBirth!.Value.Day == targetDate.Day)
            .ToList();

        if (birthdayPatients.Count == 0)
            return;

        var alreadyFired = (await db.AutoRuleFires
                .Where(f => f.AutoRuleId == rule.Id && f.FiredOnDate == todayLocal)
                .Select(f => f.PatientId)
                .ToListAsync(ct))
            .ToHashSet();

        foreach (var patient in birthdayPatients)
        {
            if (alreadyFired.Contains(patient.Id))
                continue;

            var text = rule.TextTemplate.Replace("{name}", patient.FullName, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var request = new SmsRequest
            {
                Type = RequestType.Personal,
                // Поздравление со скидкой — реклама: планировщик пропустит пациентов без согласия (Issue #34).
                Purpose = rule.Purpose,
                Title = $"Авто: {rule.Name} — {patient.FullName}",
                Status = RequestStatus.Approved,
                SendMode = SendMode.Now,
                TextTemplate = text,
                ApprovedAt = DateTime.UtcNow,
            };
            request.Recipients.Add(new RequestRecipient
            {
                PatientId = patient.Id,
                Phone = patient.Phone,
                MessageText = text,
                SmsSegments = SmsSegmentCalculator.CountSegments(text),
                SendAfter = DateTime.UtcNow,
            });

            db.SmsRequests.Add(request);
            db.AutoRuleFires.Add(new AutoRuleFire
            {
                AutoRuleId = rule.Id,
                PatientId = patient.Id,
                FiredOnDate = todayLocal,
            });

            _logger.LogInformation(
                "Авто-правило «{Rule}»: персональная заявка для {Name}", rule.Name, patient.FullName);
        }
    }
}
