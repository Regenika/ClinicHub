using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Shared;
using ClinicHub.Api.Modules.Visits;
using ClinicHub.Api.Services;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Messaging.Scenarios;

/// <summary>
/// Отправка наступивших напоминаний (ADR-0008 §4). Перед отправкой ещё раз проверяет всё, что могло измениться
/// с момента планирования, и пишет причину пропуска в StatusReason. Отправляет через существующий конвейер:
/// персональная одобренная SmsRequest → очередь SIM с её лимитами и окном отправки.
/// </summary>
public sealed class ReminderDispatcher(ClinicHubDbContext db, TimeProvider clock, ILogger<ReminderDispatcher> logger)
{
    /// <summary>Окно тишины по Москве: с 21:00 до 9:00 напоминания не уходят, ждут утра.</summary>
    public static readonly TimeSpan QuietFrom = new(21, 0, 0);
    public static readonly TimeSpan QuietTo = new(9, 0, 0);

    public static bool IsQuiet(DateTime utc)
    {
        var t = MoscowTime.ToMoscow(utc).TimeOfDay;
        return t >= QuietFrom || t < QuietTo;
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (IsQuiet(now))
            return 0;

        var due = await db.PlannedReminders
            .Include(r => r.Patient).Include(r => r.Step).Include(r => r.Scenario)
            .Where(r => r.Status == ReminderStatus.Planned && r.DueAt <= now)
            .OrderBy(r => r.DueAt).ThenBy(r => r.Id)
            .Take(200)
            .ToListAsync(ct);
        if (due.Count == 0)
            return 0;

        // Не больше одного напоминания пациенту в день (по Москве): кто уже получил сегодня — тех переносим.
        var (dayFrom, dayTo) = MoscowTime.DayRangeUtc(MoscowTime.Date(now));
        var patientIds = due.Select(r => r.PatientId).Distinct().ToList();
        var sentToday = (await db.PlannedReminders
                .Where(r => patientIds.Contains(r.PatientId) && r.Status == ReminderStatus.Sent
                            && r.SentAt >= dayFrom && r.SentAt < dayTo)
                .Select(r => r.PatientId)
                .ToListAsync(ct))
            .ToHashSet();

        var sent = new List<(PlannedReminder Reminder, SmsRequest Request)>();
        foreach (var r in due)
        {
            if (await SkipReasonAsync(r, now, ct) is { } skip)
            {
                ScenarioPlanner.Close(r, skip.Status, skip.Reason, now);
                continue;
            }

            if (sentToday.Contains(r.PatientId))
            {
                r.DueAt = NextDaySameTime(r.DueAt, now);
                r.StatusReason = "перенесено на завтра: не больше одного напоминания в день";
                r.UpdatedAt = now;
                continue;
            }

            var request = ToRequest(r, now);
            db.SmsRequests.Add(request);
            r.Status = ReminderStatus.Sent;
            r.StatusReason = null;
            r.SentAt = now;
            r.UpdatedAt = now;
            sent.Add((r, request));
            sentToday.Add(r.PatientId);
        }

        await db.SaveChangesAsync(ct);
        if (sent.Count > 0)
        {
            foreach (var (r, request) in sent)
                r.RequestId = request.Id;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Напоминания: передано в отправку {Count}", sent.Count);
        }
        return sent.Count;
    }

    /// <summary>Причина не отправлять — или null, если всё в порядке.</summary>
    private async Task<(ReminderStatus Status, string Reason)?> SkipReasonAsync(PlannedReminder r, DateTime now, CancellationToken ct)
    {
        var patient = r.Patient!;

        if (r.ScenarioId is not null && r.Scenario is { IsActive: false })
            return (ReminderStatus.Cancelled, "сценарий выключен");

        // CLAUDE.md, ADR-0008 §5: реклама — только с согласием; пока модуля согласий нет, не отправляем вовсе.
        if (r.Purpose == ScenarioPurpose.Marketing)
            return (ReminderStatus.Skipped, "нет модуля согласий (PLAN 1.2): рекламные напоминания не отправляются");

        if (!patient.IsSubscribed)
            return (ReminderStatus.Skipped, "пациент отписан от рассылок");
        if (string.IsNullOrWhiteSpace(patient.Phone))
            return (ReminderStatus.Skipped, "у пациента нет телефона");
        if (string.IsNullOrWhiteSpace(r.Text))
            return (ReminderStatus.Skipped, "пустой текст");

        if (r.Step is { } step)
        {
            if (step.CancelIfFutureVisit && await db.Visits.AnyAsync(v =>
                    v.PatientId == r.PatientId && v.Status == VisitStatus.Planned && v.StartAt > now, ct))
                return (ReminderStatus.Cancelled, "у пациента уже есть будущая запись");

            if (step.CancelIfVisitAfter && await db.Visits.AnyAsync(v =>
                    v.PatientId == r.PatientId && v.Status == VisitStatus.Completed && v.StartAt > r.EventAt
                    && v.Id != r.TriggerVisitId, ct))
                return (ReminderStatus.Cancelled, "после события пациент уже был на приёме");

            if (step.CancelIfServiceAfterId is { } serviceId && await db.Visits.AnyAsync(v =>
                    v.PatientId == r.PatientId && v.Status == VisitStatus.Completed && v.StartAt > r.EventAt
                    && v.Id != r.TriggerVisitId && v.Services.Any(s => s.ServiceId == serviceId), ct))
                return (ReminderStatus.Cancelled, $"после события уже была услуга «{serviceId}»");
        }

        return null;
    }

    /// <summary>Завтра (по Москве) в то же время, что было запланировано.</summary>
    internal static DateTime NextDaySameTime(DateTime dueUtc, DateTime nowUtc)
    {
        var time = TimeOnly.FromDateTime(MoscowTime.ToMoscow(dueUtc));
        return MoscowTime.ToUtc(MoscowTime.Date(nowUtc).AddDays(1), time);
    }

    private static SmsRequest ToRequest(PlannedReminder r, DateTime now)
    {
        var what = r.Scenario?.Name ?? "ручное";
        var title = $"Напоминание: {what} — {r.Patient!.FullName}";
        if (title.Length > 300)
            title = title[..300];

        var request = new SmsRequest
        {
            Type = RequestType.Personal,
            Title = title,
            Status = RequestStatus.Approved,
            SendMode = SendMode.Now,
            TextTemplate = r.Text,
            ApprovedAt = now,
            ExternalId = $"reminder:{r.Id}",
        };
        request.Recipients.Add(new RequestRecipient
        {
            PatientId = r.PatientId,
            Phone = r.Patient.Phone,
            MessageText = r.Text,
            SmsSegments = SmsSegmentCalculator.CountSegments(r.Text),
            SendAfter = now,
        });
        return request;
    }
}
