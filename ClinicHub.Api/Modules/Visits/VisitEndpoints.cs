using System.Globalization;
using System.Text.Json;
using ClinicHub.Api.Data;
using ClinicHub.Api.Endpoints;
using ClinicHub.Api.Modules.Catalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Visits;

/// <summary>
/// API визитов (PLAN 1.9). Пользуются администраторы на ресепшене, поэтому хватает любой роли.
/// Сопоставление внешних услуг — только Admin.
/// </summary>
public static class VisitEndpoints
{
    public static IEndpointRouteBuilder MapVisitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/visits").WithTags("Визиты").RequireAuthorization();

        group.MapGet("/", GetList);
        group.MapGet("/{id:int}", GetOne);
        group.MapPost("/", Create);
        group.MapPost("/{id:int}/complete", Complete);
        group.MapPost("/{id:int}/no-show", NoShow);
        group.MapPost("/{id:int}/cancel", Cancel);

        var map = app.MapGroup("/api/visits/service-map").WithTags("Визиты")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });
        map.MapGet("/", GetMaps);
        map.MapPut("/", UpsertMap);
        map.MapDelete("/{id:int}", DeleteMap);

        return app;
    }

    // ── Чтение ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Визиты пациента (<c>?patientId=5</c>) и/или визиты за день по Москве (<c>?date=2026-10-20</c>) — экран администратора.
    /// Можно добавить <c>&amp;status=Planned</c>.
    /// </summary>
    private static async Task<IResult> GetList(
        ClinicHubDbContext db, int? patientId, string? date, VisitStatus? status, CancellationToken ct)
    {
        if (patientId is null && string.IsNullOrEmpty(date))
            return Results.BadRequest("Укажите patientId или date (ГГГГ-ММ-ДД).");

        var query = db.Visits.AsNoTracking().Include(v => v.Services).Include(v => v.Patient).AsQueryable();

        if (patientId is int pid)
            query = query.Where(v => v.PatientId == pid);

        if (!string.IsNullOrEmpty(date))
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return Results.BadRequest("Дата в формате ГГГГ-ММ-ДД, например 2026-10-20.");
            var (from, to) = MoscowTime.DayRangeUtc(day);
            query = query.Where(v => v.StartAt >= from && v.StartAt < to);
        }

        if (status is VisitStatus st)
            query = query.Where(v => v.Status == st);

        // Для пациента — свежие сверху, для дня — по времени.
        query = patientId is not null && string.IsNullOrEmpty(date)
            ? query.OrderByDescending(v => v.StartAt)
            : query.OrderBy(v => v.StartAt);

        var visits = await query.Take(500).ToListAsync(ct);
        return Results.Ok(visits.Select(ToDto));
    }

    private static async Task<IResult> GetOne(int id, ClinicHubDbContext db, CancellationToken ct) =>
        await Load(db, id, ct) is { } v ? Results.Ok(ToDto(v)) : Results.NotFound();

    // ── Запись ──────────────────────────────────────────────────────────────

    private static async Task<IResult> Create(CreateVisitDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == dto.PatientId, ct))
            return Results.BadRequest($"Пациента #{dto.PatientId} нет.");
        if (dto.StartAt == default)
            return Results.BadRequest("Укажите время визита (startAt).");
        if (dto.Comment?.Length > 1000)
            return Results.BadRequest("Комментарий длиннее 1000 символов.");

        var visit = new Visit
        {
            PatientId = dto.PatientId,
            StartAt = dto.StartAt,
            Source = VisitSource.Manual,
            Comment = Clean(dto.Comment),
        };

        if (await ApplyDoctorAsync(visit, dto.DoctorId, dto.DoctorName, db, ct) is { } doctorError)
            return Results.BadRequest(doctorError);
        if (await ReplaceServicesAsync(visit, dto.ServiceIds ?? [], db, ct) is { } serviceError)
            return Results.BadRequest(serviceError);

        db.Visits.Add(visit);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/visits/{visit.Id}", ToDto((await Load(db, visit.Id, ct))!));
    }

    /// <summary>
    /// «Визит состоялся» (ADR-0008 §1): Planned → Completed с фактическими процедурами.
    /// Пересчитывает Patient.LastVisitAt и публикует событие «визит завершён».
    /// Из NoShow тоже можно — если неявку отметили по ошибке.
    /// </summary>
    private static async Task<IResult> Complete(
        int id, CompleteVisitDto dto, ClinicHubDbContext db, VisitEvents events, HttpContext http, CancellationToken ct)
    {
        var visit = await Load(db, id, ct, tracking: true);
        if (visit is null)
            return Results.NotFound();
        if (visit.Status is not (VisitStatus.Planned or VisitStatus.NoShow))
            return Results.Conflict($"Визит уже в статусе {visit.Status}, отметить его состоявшимся нельзя.");
        if (visit.StartAt > DateTime.UtcNow.AddHours(1))
            return Results.BadRequest("Визит ещё не начался — отметить его состоявшимся можно не раньше чем за час до начала.");
        if (dto.Comment?.Length > 1000)
            return Results.BadRequest("Комментарий длиннее 1000 символов.");

        if (dto.DoctorId is not null && await ApplyDoctorAsync(visit, dto.DoctorId, null, db, ct) is { } doctorError)
            return Results.BadRequest(doctorError);
        if (dto.ServiceIds is not null && await ReplaceServicesAsync(visit, dto.ServiceIds, db, ct) is { } serviceError)
            return Results.BadRequest(serviceError);

        SetOutcome(visit, VisitStatus.Completed, dto.Comment, http);
        await db.SaveChangesAsync(ct);
        await RecalcLastVisitAsync(db, visit.PatientId, ct);

        await events.PublishAsync(new VisitCompleted(
            visit.Id, visit.PatientId, visit.StartAt, visit.Services.Select(s => s.ServiceId).ToList(), visit.ConfirmedById), ct);

        return Results.Ok(ToDto(visit));
    }

    private static async Task<IResult> NoShow(int id, ClinicHubDbContext db, HttpContext http, CancellationToken ct) =>
        await CloseAsync(id, VisitStatus.NoShow, await ReadOptionalCommentAsync(http.Request, ct), db, http, ct);

    private static async Task<IResult> Cancel(int id, ClinicHubDbContext db, HttpContext http, CancellationToken ct) =>
        await CloseAsync(id, VisitStatus.Cancelled, await ReadOptionalCommentAsync(http.Request, ct), db, http, ct);

    /// <summary>
    /// Комментарий к неявке или отмене необязателен, поэтому тело читаем сами: если параметр-тело объявить
    /// в обработчике, ASP.NET отвечает 415 на запрос без Content-Type (кнопка «Неявка» без комментария).
    /// </summary>
    private static async Task<string?> ReadOptionalCommentAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasJsonContentType())
            return null;
        try
        {
            return (await request.ReadFromJsonAsync<VisitNoteDto>(ct))?.Comment;
        }
        catch (JsonException)
        {
            return null; // пустое или кривое тело — считаем, что комментария нет
        }
    }

    /// <summary>Неявка и отмена — только из Planned. Состоявшийся визит так не «отменить»: это исказило бы историю.</summary>
    private static async Task<IResult> CloseAsync(
        int id, VisitStatus status, string? comment, ClinicHubDbContext db, HttpContext http, CancellationToken ct)
    {
        var visit = await Load(db, id, ct, tracking: true);
        if (visit is null)
            return Results.NotFound();
        if (visit.Status != VisitStatus.Planned)
            return Results.Conflict($"Визит уже в статусе {visit.Status}.");
        if (comment?.Length > 1000)
            return Results.BadRequest("Комментарий длиннее 1000 символов.");

        SetOutcome(visit, status, comment, http);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(visit));
    }

    // ── Сопоставление внешних услуг ─────────────────────────────────────────

    private static async Task<IResult> GetMaps(ClinicHubDbContext db, VisitSource? source, CancellationToken ct)
    {
        var query = db.ExternalServiceMaps.AsQueryable();
        if (source is VisitSource src)
            query = query.Where(m => m.Source == src);
        return Results.Ok(await query
            .OrderBy(m => m.Source).ThenBy(m => m.ExternalKey)
            .Select(m => new ServiceMapDto(m.Id, m.Source, m.ExternalKey, m.ServiceId))
            .ToListAsync(ct));
    }

    /// <summary>Создать или поменять сопоставление «название во внешней системе → услуга каталога».</summary>
    private static async Task<IResult> UpsertMap(ServiceMapInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        var key = dto.ExternalKey?.Trim() ?? "";
        if (key.Length is 0 or > 300)
            return Results.BadRequest("Укажите внешнее название услуги (до 300 символов).");
        if (dto.Source == VisitSource.Manual)
            return Results.BadRequest("Сопоставление нужно только для внешних источников (Dikidi, Mis).");
        if (!await db.Services.AnyAsync(s => s.Id == dto.ServiceId, ct))
            return Results.BadRequest($"Услуги «{dto.ServiceId}» нет в каталоге.");

        var map = await db.ExternalServiceMaps.FirstOrDefaultAsync(m => m.Source == dto.Source && m.ExternalKey == key, ct);
        if (map is null)
        {
            map = new ExternalServiceMap { Source = dto.Source, ExternalKey = key };
            db.ExternalServiceMaps.Add(map);
        }
        map.ServiceId = dto.ServiceId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ServiceMapDto(map.Id, map.Source, map.ExternalKey, map.ServiceId));
    }

    private static async Task<IResult> DeleteMap(int id, ClinicHubDbContext db, CancellationToken ct)
    {
        var map = await db.ExternalServiceMaps.FindAsync([id], ct);
        if (map is null)
            return Results.NotFound();
        db.ExternalServiceMaps.Remove(map);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── Общее ───────────────────────────────────────────────────────────────

    private static Task<Visit?> Load(ClinicHubDbContext db, int id, CancellationToken ct, bool tracking = false)
    {
        var q = db.Visits.Include(v => v.Services).Include(v => v.Patient).AsQueryable();
        if (!tracking)
            q = q.AsNoTracking();
        return q.FirstOrDefaultAsync(v => v.Id == id, ct);
    }

    private static async Task<string?> ApplyDoctorAsync(
        Visit visit, string? doctorId, string? doctorName, ClinicHubDbContext db, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(doctorId))
        {
            var doctor = await db.Doctors.FindAsync([doctorId], ct);
            if (doctor is null)
                return $"Врача «{doctorId}» нет в каталоге.";
            visit.DoctorId = doctor.Id;
            visit.DoctorName = doctor.Name;
            return null;
        }

        var name = doctorName?.Trim() ?? "";
        if (name.Length > 200)
            return "Имя врача длиннее 200 символов.";
        visit.DoctorId = null;
        visit.DoctorName = name;
        return null;
    }

    /// <summary>Заменяет процедуры визита. Названия берутся из каталога и сохраняются снимком.</summary>
    private static async Task<string?> ReplaceServicesAsync(
        Visit visit, IReadOnlyList<string> serviceIds, ClinicHubDbContext db, CancellationToken ct)
    {
        var ids = serviceIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        var services = await db.Services.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var unknown = ids.Where(i => !services.ContainsKey(i)).ToList();
        if (unknown.Count > 0)
            return "Нет таких услуг в каталоге: " + string.Join(", ", unknown);

        visit.Services.RemoveAll(s => !ids.Contains(s.ServiceId));
        foreach (var sid in ids.Where(sid => visit.Services.All(s => s.ServiceId != sid)))
            visit.Services.Add(new VisitService { ServiceId = sid, ServiceName = services[sid] });
        return null;
    }

    private static void SetOutcome(Visit visit, VisitStatus status, string? comment, HttpContext http)
    {
        visit.Status = status;
        visit.ConfirmedById = http.User.Identity?.IsAuthenticated == true ? http.User.GetUserId() : null;
        visit.ConfirmedAt = DateTime.UtcNow;
        visit.UpdatedAt = DateTime.UtcNow;
        if (Clean(comment) is { } c)
            visit.Comment = c;
    }

    /// <summary>Patient.LastVisitAt — по последнему состоявшемуся визиту (ADR-0008 §1).</summary>
    private static async Task RecalcLastVisitAsync(ClinicHubDbContext db, int patientId, CancellationToken ct)
    {
        var last = await db.Visits
            .Where(v => v.PatientId == patientId && v.Status == VisitStatus.Completed)
            .MaxAsync(v => (DateTime?)v.StartAt, ct);
        var patient = await db.Patients.FindAsync([patientId], ct);
        if (patient is not null && patient.LastVisitAt != last)
        {
            patient.LastVisitAt = last;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static VisitDto ToDto(Visit v) => new(
        v.Id, v.PatientId, v.Patient?.FullName, v.StartAt, v.DoctorId, v.DoctorName, v.Status, v.Source, v.ExternalId,
        v.Comment, v.ConfirmedById, v.ConfirmedAt,
        v.Services.OrderBy(s => s.ServiceName).Select(s => new VisitServiceDto(s.ServiceId, s.ServiceName)).ToList());
}

/// <summary>Границы дня по Москве в UTC. Как и в ClinicHub.Web, с запасными вариантами для Windows без ICU.</summary>
internal static class MoscowTime
{
    private static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Europe/Moscow", "Russian Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
                return zone;
        }
        return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "Москва", "Москва");
    }

    public static (DateTime FromUtc, DateTime ToUtc) DayRangeUtc(DateOnly day)
    {
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var end = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(start, Zone), TimeZoneInfo.ConvertTimeToUtc(end, Zone));
    }
}
