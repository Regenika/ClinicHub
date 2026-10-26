using System.Globalization;
using System.Security.Claims;
using ClinicHub.Api.Data;
using ClinicHub.Api.Endpoints;
using ClinicHub.Api.Modules.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Messaging.Scenarios;

/// <summary>
/// API сценариев (правка — роль Admin) и напоминаний (любая роль: ими пользуется ресепшен).
/// </summary>
public static class ScenarioEndpoints
{
    public static IEndpointRouteBuilder MapScenarioEndpoints(this IEndpointRouteBuilder app)
    {
        var scenarios = app.MapGroup("/api/scenarios").WithTags("Сценарии напоминаний").RequireAuthorization();
        scenarios.MapGet("/", GetScenarios);
        scenarios.MapGet("/{id:int}", GetScenario);
        scenarios.MapPost("/preview", Preview);
        scenarios.MapPost("/{id:int}/apply", Apply);

        var admin = app.MapGroup("/api/scenarios").WithTags("Сценарии напоминаний")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });
        admin.MapPost("/", CreateScenario);
        admin.MapPut("/{id:int}", UpdateScenario);
        admin.MapDelete("/{id:int}", DeleteScenario);

        var reminders = app.MapGroup("/api/reminders").WithTags("Напоминания").RequireAuthorization();
        reminders.MapGet("/", GetReminders);
        reminders.MapPost("/", CreateManual);
        reminders.MapPut("/{id:int}", UpdateReminder);
        reminders.MapPost("/{id:int}/cancel", CancelReminder);

        return app;
    }

    // ── Сценарии ────────────────────────────────────────────────────────────

    private static async Task<IResult> GetScenarios(ClinicHubDbContext db, CancellationToken ct) =>
        Results.Ok((await Query(db).OrderBy(s => s.Name).ToListAsync(ct)).Select(ToDto));

    private static async Task<IResult> GetScenario(int id, ClinicHubDbContext db, CancellationToken ct) =>
        await Query(db).FirstOrDefaultAsync(s => s.Id == id, ct) is { } s ? Results.Ok(ToDto(s)) : Results.NotFound();

    private static async Task<IResult> CreateScenario(ScenarioInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        var s = new CareScenario();
        if (await ApplyAsync(s, dto, db, ct) is { } error)
            return Results.BadRequest(error);
        db.CareScenarios.Add(s);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/scenarios/{s.Id}", ToDto(s));
    }

    /// <summary>
    /// Правка сценария не трогает уже запланированные напоминания: у них свой текст и дата, их видно и можно поправить.
    /// Выключение сценария отменит его напоминания при отправке («сценарий выключен»).
    /// </summary>
    private static async Task<IResult> UpdateScenario(
        int id, ScenarioInputDto dto, ClinicHubDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var s = await db.CareScenarios.Include(x => x.Steps).Include(x => x.TriggerServices).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
            return Results.NotFound();

        // Удалённые шаги: отвязываем от них напоминания, внешний ключ — «без действия».
        var keptStepIds = (dto.Steps ?? []).Where(x => x.Id is not null).Select(x => x.Id!.Value).ToHashSet();
        var removed = s.Steps.Where(x => !keptStepIds.Contains(x.Id)).Select(x => x.Id).ToList();
        if (removed.Count > 0)
        {
            foreach (var r in await db.PlannedReminders.Where(r => r.StepId != null && removed.Contains(r.StepId.Value)).ToListAsync(ct))
                r.StepId = null;
        }

        if (await ApplyAsync(s, dto, db, ct) is { } error)
            return Results.BadRequest(error);
        s.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(s));
    }

    /// <summary>Удаление сценария: его запланированные напоминания отменяются, история (отправленные) остаётся.</summary>
    private static async Task<IResult> DeleteScenario(int id, ClinicHubDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var s = await db.CareScenarios.FindAsync([id], ct);
        if (s is null)
            return Results.NotFound();

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var r in await db.PlannedReminders.Where(r => r.ScenarioId == id).ToListAsync(ct))
        {
            if (r.Status == ReminderStatus.Planned)
                ScenarioPlanner.Close(r, ReminderStatus.Cancelled, $"сценарий «{s.Name}» удалён", now);
            r.ScenarioId = null;
            r.StepId = null;
        }
        db.CareScenarios.Remove(s);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Применить сценарий к пациенту вручную (событие — сейчас).</summary>
    private static async Task<IResult> Apply(
        int id, ApplyScenarioDto dto, ClinicHubDbContext db, ScenarioPlanner planner, ClaimsPrincipal user, CancellationToken ct)
    {
        var s = await db.CareScenarios.Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
            return Results.NotFound();
        if (!s.IsActive)
            return Results.BadRequest("Сценарий выключен.");
        var patient = await db.Patients.FindAsync([dto.PatientId], ct);
        if (patient is null)
            return Results.BadRequest($"Пациента #{dto.PatientId} нет.");

        var created = await planner.ApplyAsync(s, patient, user.GetUserId(), ct);
        return Results.Ok(created.Select(r => ToDto(r, patient.FullName, s.Name)));
    }

    /// <summary>«Что запланируется, если подтвердить этот визит» — для экрана «пациент уходит». Ничего не сохраняет.</summary>
    private static async Task<IResult> Preview(PreviewDto dto, ClinicHubDbContext db, ScenarioPlanner planner, CancellationToken ct)
    {
        var visit = await db.Visits.Include(v => v.Services).Include(v => v.Patient).FirstOrDefaultAsync(v => v.Id == dto.VisitId, ct);
        if (visit?.Patient is null)
            return Results.NotFound();

        var serviceIds = dto.ServiceIds ?? visit.Services.Select(s => s.ServiceId).ToList();
        var drafts = await planner.DraftsForVisitAsync(visit.Patient, visit.StartAt, serviceIds, ct);
        return Results.Ok(drafts.Select(d => new ReminderDraftDto(
            d.ScenarioId, d.ScenarioName, d.Purpose, d.DueAt, d.Text,
            d.Purpose == ScenarioPurpose.Marketing ? "рекламное: не уйдёт, пока нет модуля согласий" : null)));
    }

    // ── Напоминания ─────────────────────────────────────────────────────────

    /// <summary>Напоминания пациента (<c>?patientId=</c>) и/или по статусу (<c>?status=Planned</c>), свежие по сроку сверху.</summary>
    private static async Task<IResult> GetReminders(
        ClinicHubDbContext db, int? patientId, ReminderStatus? status, CancellationToken ct)
    {
        if (patientId is null && status is null)
            return Results.BadRequest("Укажите patientId и/или status.");

        var q = db.PlannedReminders.AsNoTracking().Include(r => r.Patient).Include(r => r.Scenario).AsQueryable();
        if (patientId is int pid)
            q = q.Where(r => r.PatientId == pid);
        if (status is ReminderStatus st)
            q = q.Where(r => r.Status == st);

        var list = await q.OrderByDescending(r => r.DueAt).ThenByDescending(r => r.Id).Take(500).ToListAsync(ct);
        return Results.Ok(list.Select(r => ToDto(r, r.Patient?.FullName, r.Scenario?.Name)));
    }

    private static async Task<IResult> CreateManual(
        ManualReminderDto dto, ClinicHubDbContext db, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        var patient = await db.Patients.FindAsync([dto.PatientId], ct);
        if (patient is null)
            return Results.BadRequest($"Пациента #{dto.PatientId} нет.");
        if (ValidateText(dto.Text) is { } textError)
            return Results.BadRequest(textError);
        if (!Enum.IsDefined(dto.Purpose))
            return Results.BadRequest("Purpose: Service или Marketing.");

        var now = clock.GetUtcNow().UtcDateTime;
        DateTime due;
        if (dto.DueAt is DateTime exact)
        {
            due = DateTime.SpecifyKind(exact.Kind == DateTimeKind.Local ? exact.ToUniversalTime() : exact, DateTimeKind.Utc);
        }
        else if (dto.InDays is int days and >= 0 and <= 3650)
        {
            var window = await SendWindow.LoadAsync(db, ct);
            if (CheckTime(dto.Time ?? "11:00", window, "Время напоминания") is { } timeError)
                return Results.BadRequest(timeError);
            var time = ParseTime(dto.Time ?? "11:00")!.Value;
            due = MoscowTime.ToUtc(MoscowTime.Date(now).AddDays(days), time);
        }
        else
        {
            return Results.BadRequest("Укажите inDays (0–3650) или dueAt.");
        }

        var r = new PlannedReminder
        {
            PatientId = patient.Id, EventAt = now, Kind = ReminderKind.Manual, Purpose = dto.Purpose,
            DueAt = due, Text = ReminderText.Render(dto.Text, patient), CreatedById = user.GetUserId(),
        };
        db.PlannedReminders.Add(r);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/reminders/{r.Id}", ToDto(r, patient.FullName, null));
    }

    private static async Task<IResult> UpdateReminder(
        int id, ReminderUpdateDto dto, ClinicHubDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var r = await db.PlannedReminders.Include(x => x.Patient).Include(x => x.Scenario).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
            return Results.NotFound();
        if (r.Status != ReminderStatus.Planned)
            return Results.Conflict($"Напоминание уже в статусе {r.Status}, менять его нельзя.");

        if (dto.Text is not null)
        {
            if (ValidateText(dto.Text) is { } textError)
                return Results.BadRequest(textError);
            r.Text = dto.Text.Trim();
        }
        if (dto.DueAt is DateTime due)
            r.DueAt = DateTime.SpecifyKind(due.Kind == DateTimeKind.Local ? due.ToUniversalTime() : due, DateTimeKind.Utc);

        r.StatusReason = null;
        r.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(r, r.Patient?.FullName, r.Scenario?.Name));
    }

    private static async Task<IResult> CancelReminder(int id, ClinicHubDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var r = await db.PlannedReminders.Include(x => x.Patient).Include(x => x.Scenario).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null)
            return Results.NotFound();
        if (r.Status != ReminderStatus.Planned)
            return Results.Conflict($"Напоминание уже в статусе {r.Status}.");

        ScenarioPlanner.Close(r, ReminderStatus.Cancelled, "отменено вручную", clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(r, r.Patient?.FullName, r.Scenario?.Name));
    }

    // ── Общее ───────────────────────────────────────────────────────────────

    private static IQueryable<CareScenario> Query(ClinicHubDbContext db) =>
        db.CareScenarios.AsNoTracking().Include(s => s.Steps).Include(s => s.TriggerServices);

    private static async Task<string?> ApplyAsync(CareScenario s, ScenarioInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        var name = dto.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200)
            return "Укажите название сценария (до 200 символов).";
        if (!Enum.IsDefined(dto.Purpose) || !Enum.IsDefined(dto.Trigger))
            return "Purpose: Service | Marketing; Trigger: VisitCompleted | NoVisitFor | Birthday | Manual.";
        if (dto.Trigger == ScenarioTrigger.NoVisitFor && dto.NoVisitDays is not (>= 7 and <= 3650))
            return "Для «давно не были» укажите noVisitDays от 7 до 3650.";
        if (dto.Trigger == ScenarioTrigger.Birthday)
            return "Сценарии по дню рождения пока не поддерживаются: работает авто-правило «день рождения».";

        var steps = dto.Steps ?? [];
        if (steps.Count is 0 or > 20)
            return "У сценария должно быть от 1 до 20 шагов.";

        var serviceIds = (dto.TriggerServiceIds ?? []).Distinct().ToList();
        var referenced = serviceIds.Concat(steps.Where(x => x.CancelIfServiceAfterId is not null).Select(x => x.CancelIfServiceAfterId!)).Distinct().ToList();
        if (referenced.Count > 0)
        {
            var known = await db.Services.Where(x => referenced.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            var unknown = referenced.Except(known).ToList();
            if (unknown.Count > 0)
                return "Нет таких услуг в каталоге: " + string.Join(", ", unknown);
        }

        var window = await SendWindow.LoadAsync(db, ct);
        var parsed = new List<(ScenarioStepDto Dto, TimeOnly Time)>();
        foreach (var st in steps)
        {
            if (st.OffsetDays is < 0 or > 3650)
                return "Смещение шага — от 0 до 3650 дней.";
            if (CheckTime(st.SendTime, window, "Время шага") is { } timeError)
                return timeError;
            var time = ParseTime(st.SendTime)!.Value;
            if (ValidateText(st.Text) is { } textError)
                return textError;
            if (st.Id is int sid && s.Steps.All(x => x.Id != sid))
                return $"Шага #{sid} нет в этом сценарии.";
            parsed.Add((st, time));
        }

        s.Name = name;
        s.IsActive = dto.IsActive;
        s.Purpose = dto.Purpose;
        s.Trigger = dto.Trigger;
        s.NoVisitDays = dto.Trigger == ScenarioTrigger.NoVisitFor ? dto.NoVisitDays : null;

        s.TriggerServices.RemoveAll(x => !serviceIds.Contains(x.ServiceId));
        foreach (var sid in serviceIds.Where(sid => s.TriggerServices.All(x => x.ServiceId != sid)))
            s.TriggerServices.Add(new CareScenarioService { ServiceId = sid });
        if (dto.Trigger != ScenarioTrigger.VisitCompleted)
            s.TriggerServices.Clear();

        // Шаги: с Id — правим на месте, без Id — новые, отсутствующие в запросе — удаляем.
        var keep = parsed.Where(p => p.Dto.Id is not null).Select(p => p.Dto.Id!.Value).ToHashSet();
        s.Steps.RemoveAll(x => !keep.Contains(x.Id));
        foreach (var (st, time) in parsed)
        {
            var step = st.Id is int id ? s.Steps.First(x => x.Id == id) : new CareScenarioStep();
            step.Order = st.Order;
            step.OffsetDays = st.OffsetDays;
            step.SendTime = time.ToTimeSpan();
            step.Text = st.Text.Trim();
            step.CancelIfFutureVisit = st.CancelIfFutureVisit;
            step.CancelIfVisitAfter = st.CancelIfVisitAfter;
            step.CancelIfServiceAfterId = string.IsNullOrWhiteSpace(st.CancelIfServiceAfterId) ? null : st.CancelIfServiceAfterId;
            if (st.Id is null)
                s.Steps.Add(step);
        }
        return null;
    }

    private static string? ValidateText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "Укажите текст напоминания."
        : text.Length > 1000 ? "Текст длиннее 1000 символов."
        : null;

    /// <summary>«ЧЧ:ММ» по Москве.</summary>
    private static TimeOnly? ParseTime(string? s) =>
        TimeOnly.TryParseExact(s?.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;

    /// <summary>
    /// Ошибка для администратора или null. Время вне окна отправки не переносим молча, а отвечаем 400:
    /// сразу видно, почему напоминание не уйдёт в назначенный час.
    /// </summary>
    private static string? CheckTime(string? s, SendWindow window, string what) =>
        ParseTime(s) is not { } t
            ? $"{what} «{s}»: нужно «ЧЧ:ММ», например 11:00."
            : window.Allows(t)
                ? null
                : $"{what} {t.ToString("HH:mm", CultureInfo.InvariantCulture)} вне окна отправки {window} (настройки отправки). " +
                  $"Выберите время с {window.From.ToString(@"hh\:mm")} до {window.To.Add(TimeSpan.FromMinutes(-1)).ToString(@"hh\:mm")}.";

    private static ScenarioDto ToDto(CareScenario s) => new(
        s.Id, s.Name, s.IsActive, s.Purpose, s.Trigger, s.NoVisitDays,
        s.TriggerServices.Select(x => x.ServiceId).OrderBy(x => x).ToList(),
        s.Steps.OrderBy(x => x.Order).ThenBy(x => x.Id).Select(x => new ScenarioStepDto(
            x.Id, x.Order, x.OffsetDays, TimeOnly.FromTimeSpan(x.SendTime).ToString("HH:mm", CultureInfo.InvariantCulture), x.Text,
            x.CancelIfFutureVisit, x.CancelIfVisitAfter, x.CancelIfServiceAfterId)).ToList(),
        s.CreatedAt, s.UpdatedAt);

    private static ReminderDto ToDto(PlannedReminder r, string? patientName, string? scenarioName) => new(
        r.Id, r.PatientId, patientName, r.ScenarioId, scenarioName, r.StepId, r.TriggerVisitId,
        r.Kind, r.Purpose, r.DueAt, r.Text, r.Status, r.StatusReason, r.RequestId, r.SentAt, r.CreatedAt);
}
