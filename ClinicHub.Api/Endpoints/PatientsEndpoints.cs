using ClinicHub.Core.Dtos;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Endpoints;

public static class PatientsEndpoints
{
    public static IEndpointRouteBuilder MapPatientsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/patients")
            .WithTags("Пациенты")
            .RequireAuthorization();

        group.MapGet("/", GetList);
        group.MapGet("/audience", GetAudience);
        group.MapGet("/{id:int}", GetById);
        group.MapPost("/", Create);
        group.MapPut("/{id:int}", Update);
        group.MapDelete("/{id:int}", Delete);

        return app;
    }

    /// <summary>
    /// Кому слать рассылку (мастер веб-панели, ADR-0012): all — все с телефоном; recent — были за months месяцев;
    /// lost — не были дольше months месяцев (и нет будущей записи); service — делали услугу serviceId (по каталогу);
    /// birthday — день рождения в месяце month (1–12). Возвращает id пациентов; согласия проверит расчёт рассылки.
    /// </summary>
    private static async Task<IResult> GetAudience(
        Data.ClinicHubDbContext db, string? kind, int? months, string? serviceId, int? month, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var query = db.Patients.Where(p => p.Phone != "");
        var m = Math.Clamp(months ?? 6, 1, 120);
        switch ((kind ?? "all").ToLowerInvariant())
        {
            case "all":
                break;
            case "recent":
                var since = now.AddMonths(-m);
                query = query.Where(p => p.LastVisitAt != null && p.LastVisitAt >= since);
                break;
            case "lost":
                var before = now.AddMonths(-m);
                query = query.Where(p => p.LastVisitAt != null && p.LastVisitAt < before
                    && !db.Visits.Any(v => v.PatientId == p.Id && v.StartAt > now
                        && v.Status == Modules.Visits.VisitStatus.Planned));
                break;
            case "service":
                if (string.IsNullOrWhiteSpace(serviceId))
                    return Results.BadRequest("Выберите услугу.");
                query = query.Where(p => db.VisitServices.Any(vs => vs.ServiceId == serviceId
                    && vs.Visit!.PatientId == p.Id && vs.Visit.Status == Modules.Visits.VisitStatus.Completed));
                break;
            case "birthday":
                if (month is not (>= 1 and <= 12))
                    return Results.BadRequest("Укажите месяц от 1 до 12.");
                // Месяц считаем в памяти: у даты рождения конвертер значения, и .Month в SQL не переводится.
                var births = await query.Where(p => p.DateOfBirth != null)
                    .Select(p => new { p.Id, p.DateOfBirth }).ToListAsync(ct);
                var byMonth = births.Where(b => b.DateOfBirth!.Value.Month == month).Select(b => b.Id).OrderBy(i => i).ToList();
                return Results.Ok(new { count = byMonth.Count, ids = byMonth });
            default:
                return Results.BadRequest("kind: all, recent, lost, service или birthday.");
        }

        var ids = await query.OrderBy(p => p.Id).Select(p => p.Id).Take(20000).ToListAsync(ct);
        return Results.Ok(new { count = ids.Count, ids });
    }

    private static async Task<IResult> GetList(
        Data.ClinicHubDbContext db,
        string? search,
        bool? subscribed,
        int? ageMin,
        int? ageMax,
        int? previousRequestId,
        int? take)
    {
        var query = db.Patients.AsQueryable();

        if (subscribed is bool s)
            query = query.Where(p => p.IsSubscribed == s);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Без учёта регистра на любой СУБД: в SQL Server Contains и так не учитывает регистр,
            // а в PostgreSQL учитывает. ToLower переводится в LOWER() и там, и там (ADR-0009: только LINQ).
            var term = search.Trim().ToLower();
            query = query.Where(p =>
                p.FullName.ToLower().Contains(term) || p.Phone.Contains(term));
        }

        // Фильтр "аудитория прошлой заявки": оставить только тех, кто уже получал конкретную рассылку.
        if (previousRequestId is int reqId)
        {
            query = query.Where(p => db.RequestRecipients
                .Any(r => r.RequestId == reqId && r.PatientId == p.Id));
        }

        var patients = await query
            .OrderBy(p => p.FullName)
            .Take(Math.Min(take ?? 500, 2000))
            .Select(p => new PatientDto
            {
                Id = p.Id,
                FullName = p.FullName,
                Phone = p.Phone,
                Email = p.Email,
                DateOfBirth = p.DateOfBirth,
                IsSubscribed = p.IsSubscribed,
                Notes = p.Notes,
                LastVisitAt = p.LastVisitAt,
            })
            .ToListAsync();

        // Возраст считаем на сервере в памяти (SQL Server плохо переводит DateTime-арифметику для лет).
        var today = DateTime.UtcNow.Date;
        foreach (var p in patients)
            p.Age = p.DateOfBirth is DateTime dob ? CalculateAge(dob, today) : null;

        if (ageMin is int minA)
            patients = patients.Where(p => p.Age is int a && a >= minA).ToList();
        if (ageMax is int maxA)
            patients = patients.Where(p => p.Age is int a && a <= maxA).ToList();

        return Results.Ok(patients);
    }

    private static int CalculateAge(DateTime dob, DateTime today)
    {
        var age = today.Year - dob.Year;
        if (dob.Date > today.AddYears(-age))
            age--;
        return age;
    }

    private static async Task<IResult> GetById(int id, Data.ClinicHubDbContext db)
    {
        var patient = await db.Patients.FindAsync(id);
        return patient is null ? Results.NotFound() : Results.Ok(ToDto(patient));
    }

    private static async Task<IResult> Create(
        PatientInputDto dto,
        Data.ClinicHubDbContext db,
        CancellationToken ct)
    {
        Validate(dto, out var error);
        if (error != null)
            return Results.BadRequest(error);

        var patient = new Patient
        {
            FullName = dto.FullName.Trim(),
            Phone = dto.Phone.Trim(),
            Email = dto.Email,
            DateOfBirth = dto.DateOfBirth,
            IsSubscribed = dto.IsSubscribed,
            Notes = dto.Notes,
        };

        db.Patients.Add(patient);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/patients/{patient.Id}", ToDto(patient));
    }

    private static async Task<IResult> Update(
        int id,
        PatientInputDto dto,
        Data.ClinicHubDbContext db,
        CancellationToken ct)
    {
        var patient = await db.Patients.FindAsync(id);
        if (patient is null)
            return Results.NotFound();

        Validate(dto, out var error);
        if (error != null)
            return Results.BadRequest(error);

        patient.FullName = dto.FullName.Trim();
        patient.Phone = dto.Phone.Trim();
        patient.Email = dto.Email;
        patient.DateOfBirth = dto.DateOfBirth;
        patient.IsSubscribed = dto.IsSubscribed;
        patient.Notes = dto.Notes;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(patient));
    }

    private static async Task<IResult> Delete(int id, Data.ClinicHubDbContext db, CancellationToken ct)
    {
        var patient = await db.Patients.FindAsync(id);
        if (patient is null)
            return Results.NotFound();

        db.Patients.Remove(patient);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void Validate(PatientInputDto dto, out string? error)
    {
        error = string.IsNullOrWhiteSpace(dto.FullName)
            ? "Укажите ФИО."
            : string.IsNullOrWhiteSpace(dto.Phone)
                ? "Укажите телефон."
                : null;
    }

    private static PatientDto ToDto(Patient p) => new()
    {
        Id = p.Id,
        FullName = p.FullName,
        Phone = p.Phone,
        Email = p.Email,
        DateOfBirth = p.DateOfBirth,
        Age = p.DateOfBirth is DateTime dob ? CalculateAge(dob, DateTime.UtcNow.Date) : null,
        IsSubscribed = p.IsSubscribed,
        Notes = p.Notes,
        LastVisitAt = p.LastVisitAt,
    };
}