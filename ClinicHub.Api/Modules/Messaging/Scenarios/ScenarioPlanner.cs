using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Shared;
using ClinicHub.Api.Modules.Visits;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Messaging.Scenarios;

/// <summary>Одно напоминание, которое будет запланировано (для предпросмотра и для сохранения).</summary>
public sealed record ReminderDraft(int ScenarioId, string ScenarioName, int StepId, ScenarioPurpose Purpose, DateTime DueAt, string Text);

/// <summary>
/// Планировщик сценариев (ADR-0008 §4). Только создаёт и отменяет напоминания; отправляет их ReminderDispatcher.
/// Подписан на «визит завершён» (IVisitCompletedHandler), плюс периодический поиск «давно не были».
/// </summary>
public sealed class ScenarioPlanner(ClinicHubDbContext db, TimeProvider clock, ILogger<ScenarioPlanner> logger)
    : IVisitCompletedHandler
{
    public Task HandleAsync(VisitCompleted e, CancellationToken ct) =>
        PlanForVisitAsync(e.VisitId, e.PatientId, e.StartAtUtc, e.ServiceIds, ct);

    /// <summary>
    /// По всем активным сценариям VisitCompleted, подходящим по услугам, создаёт напоминания.
    /// Незавершённые напоминания тех же сценариев у пациента отменяются: цепочка начинается заново.
    /// </summary>
    public async Task<int> PlanForVisitAsync(
        int visitId, int patientId, DateTime visitStartUtc, IReadOnlyList<string> serviceIds, CancellationToken ct)
    {
        var patient = await db.Patients.FindAsync([patientId], ct);
        if (patient is null)
            return 0;

        var drafts = await DraftsForVisitAsync(patient, visitStartUtc, serviceIds, ct);
        var scenarioIds = drafts.Select(d => d.ScenarioId).Distinct().ToList();
        var now = clock.GetUtcNow().UtcDateTime;

        if (scenarioIds.Count > 0)
        {
            var old = await db.PlannedReminders
                .Where(r => r.PatientId == patientId && r.Status == ReminderStatus.Planned
                            && r.ScenarioId != null && scenarioIds.Contains(r.ScenarioId.Value))
                .ToListAsync(ct);
            foreach (var r in old)
                Close(r, ReminderStatus.Cancelled, $"цепочка перезапущена визитом #{visitId}", now);
        }

        foreach (var d in drafts)
        {
            db.PlannedReminders.Add(new PlannedReminder
            {
                PatientId = patientId,
                ScenarioId = d.ScenarioId,
                StepId = d.StepId,
                TriggerVisitId = visitId,
                EventAt = visitStartUtc,
                Kind = ReminderKind.Scenario,
                Purpose = d.Purpose,
                DueAt = d.DueAt,
                Text = d.Text,
            });
        }

        await db.SaveChangesAsync(ct);
        if (drafts.Count > 0)
            logger.LogInformation("Визит #{VisitId}: запланировано напоминаний {Count} по сценариям {Scenarios}",
                visitId, drafts.Count, string.Join(", ", scenarioIds));
        return drafts.Count;
    }

    /// <summary>Что запланируется, если подтвердить визит с этими услугами. Ничего не сохраняет.</summary>
    public async Task<List<ReminderDraft>> DraftsForVisitAsync(
        Patient patient, DateTime visitStartUtc, IReadOnlyList<string> serviceIds, CancellationToken ct)
    {
        var scenarios = await db.CareScenarios
            .Include(s => s.Steps).Include(s => s.TriggerServices)
            .Where(s => s.IsActive && s.Trigger == ScenarioTrigger.VisitCompleted)
            .AsNoTracking()
            .ToListAsync(ct);

        return scenarios
            .Where(s => s.TriggerServices.Count == 0 || s.TriggerServices.Any(t => serviceIds.Contains(t.ServiceId)))
            .SelectMany(s => Drafts(s, patient, visitStartUtc))
            .OrderBy(d => d.DueAt)
            .ToList();
    }

    /// <summary>
    /// «Давно не были» (ADR-0008 §3): пациенты, у которых последний визит старше N дней, нет будущей записи
    /// и по этому сценарию после последнего визита ещё ничего не планировалось. Безопасно запускать часто.
    /// </summary>
    public async Task<int> RunNoVisitScanAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var scenarios = await db.CareScenarios.Include(s => s.Steps)
            .Where(s => s.IsActive && s.Trigger == ScenarioTrigger.NoVisitFor && s.NoVisitDays != null)
            .AsNoTracking()
            .ToListAsync(ct);

        var created = 0;
        foreach (var scenario in scenarios)
        {
            var border = now.AddDays(-scenario.NoVisitDays!.Value);
            var patients = await db.Patients
                .Where(p => p.LastVisitAt != null && p.LastVisitAt <= border)
                .Where(p => !db.Visits.Any(v => v.PatientId == p.Id && v.Status == VisitStatus.Planned && v.StartAt > now))
                .Where(p => !db.PlannedReminders.Any(r =>
                    r.PatientId == p.Id && r.ScenarioId == scenario.Id && r.EventAt >= p.LastVisitAt))
                .Take(500)
                .ToListAsync(ct);

            foreach (var patient in patients)
            {
                foreach (var d in Drafts(scenario, patient, now))
                {
                    db.PlannedReminders.Add(new PlannedReminder
                    {
                        PatientId = patient.Id, ScenarioId = d.ScenarioId, StepId = d.StepId,
                        // Событие — момент последнего визита: по нему и проверяем «был визит после события».
                        EventAt = patient.LastVisitAt!.Value,
                        Kind = ReminderKind.Scenario, Purpose = d.Purpose, DueAt = d.DueAt, Text = d.Text,
                    });
                    created++;
                }
            }
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("«Давно не были»: запланировано напоминаний {Count}", created);
        }
        return created;
    }

    /// <summary>Применить сценарий к пациенту вручную (событие — сейчас). Незавершённые этого же сценария отменяются.</summary>
    public async Task<List<PlannedReminder>> ApplyAsync(CareScenario scenario, Patient patient, int? userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var old = await db.PlannedReminders
            .Where(r => r.PatientId == patient.Id && r.ScenarioId == scenario.Id && r.Status == ReminderStatus.Planned)
            .ToListAsync(ct);
        foreach (var r in old)
            Close(r, ReminderStatus.Cancelled, "сценарий применён к пациенту заново", now);

        var reminders = Drafts(scenario, patient, now).Select(d => new PlannedReminder
        {
            PatientId = patient.Id, ScenarioId = d.ScenarioId, StepId = d.StepId, EventAt = now,
            Kind = ReminderKind.Scenario, Purpose = d.Purpose, DueAt = d.DueAt, Text = d.Text, CreatedById = userId,
        }).ToList();
        db.PlannedReminders.AddRange(reminders);
        await db.SaveChangesAsync(ct);
        return reminders;
    }

    /// <summary>Шаги сценария → черновики: дата события по Москве + N дней, в заданное время по Москве.</summary>
    public static IEnumerable<ReminderDraft> Drafts(CareScenario scenario, Patient patient, DateTime eventUtc)
    {
        var eventDay = MoscowTime.Date(eventUtc);
        foreach (var step in scenario.Steps.OrderBy(s => s.Order).ThenBy(s => s.Id))
        {
            var due = MoscowTime.ToUtc(eventDay.AddDays(step.OffsetDays), TimeOnly.FromTimeSpan(step.SendTime));
            yield return new ReminderDraft(
                scenario.Id, scenario.Name, step.Id, scenario.Purpose, due, ReminderText.Render(step.Text, patient));
        }
    }

    internal static void Close(PlannedReminder r, ReminderStatus status, string reason, DateTime nowUtc)
    {
        r.Status = status;
        r.StatusReason = reason;
        r.UpdatedAt = nowUtc;
    }
}

/// <summary>Подстановки в текст напоминания. Пока только имя: диагноз и процедуру в текст не подставляем (ADR-0008 §5).</summary>
public static class ReminderText
{
    public static string Render(string template, Patient patient) =>
        template.Replace("{name}", patient.FullName.Trim(), StringComparison.OrdinalIgnoreCase).Trim();
}
