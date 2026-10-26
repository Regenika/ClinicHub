using System.Security.Claims;
using System.Text.RegularExpressions;
using ClinicHub.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Catalog;

/// <summary>
/// API каталога (PLAN 1.8).
///   /api/catalog/...        — чтение для сайта, без авторизации, только видимое на сайте;
///   /api/catalog/admin/...  — полный список и правка, только роль Admin.
/// </summary>
public static partial class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var pub = app.MapGroup("/api/catalog").WithTags("Каталог (сайт)").AllowAnonymous();
        pub.MapGet("/categories", GetPublicCategories);
        pub.MapGet("/services", GetPublicServices);
        pub.MapGet("/doctors", GetPublicDoctors);

        var admin = app.MapGroup("/api/catalog/admin").WithTags("Каталог (админка)")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });

        admin.MapGet("/categories", GetCategories);
        admin.MapPost("/categories", CreateCategory);
        admin.MapPut("/categories/{id:int}", UpdateCategory);
        admin.MapDelete("/categories/{id:int}", DeleteCategory);

        admin.MapGet("/services", GetServices);
        admin.MapGet("/services/{id}", GetService);
        admin.MapPost("/services", CreateService);
        admin.MapPut("/services/{id}", UpdateService);
        admin.MapDelete("/services/{id}", DeleteService);
        admin.MapGet("/services/{id}/price-history", GetPriceHistory);

        admin.MapGet("/doctors", GetDoctors);
        admin.MapGet("/doctors/{id}", GetDoctor);
        admin.MapPost("/doctors", CreateDoctor);
        admin.MapPut("/doctors/{id}", UpdateDoctor);
        admin.MapDelete("/doctors/{id}", DeleteDoctor);

        return app;
    }

    // ── Для сайта ───────────────────────────────────────────────────────────

    private static async Task<IResult> GetPublicCategories(ClinicHubDbContext db, CancellationToken ct) =>
        Results.Ok(await db.ServiceCategories
            .Where(c => c.IsVisibleOnSite)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new PublicCategoryDto(c.Id, c.Name, c.SortOrder))
            .ToListAsync(ct));

    private static async Task<IResult> GetPublicServices(ClinicHubDbContext db, CancellationToken ct)
    {
        var services = await db.Services
            .Include(s => s.Category)
            // Услуга из скрытой категории тоже скрыта.
            .Where(s => s.IsVisibleOnSite && (s.Category == null || s.Category.IsVisibleOnSite))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .AsNoTracking()
            .ToListAsync(ct);
        return Results.Ok(services.Select(s => new PublicServiceDto(
            s.Id, s.CategoryId, s.Category?.Name, s.Name, s.Description,
            s.PriceFrom, s.PriceTo, s.PriceDisplay, s.PriceUnit, PriceFormat.Text(s),
            s.DurationMinutes, s.SortOrder)));
    }

    private static async Task<IResult> GetPublicDoctors(ClinicHubDbContext db, CancellationToken ct)
    {
        var doctors = await db.Doctors
            .Where(d => d.IsVisibleOnSite)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name)
            .Select(d => new
            {
                d.Id, d.Name, d.Role, d.Description, d.SortOrder,
                // Сайту отдаём только видимые услуги врача.
                ServiceIds = d.Services.Where(x => x.Service!.IsVisibleOnSite).Select(x => x.ServiceId).ToList(),
            })
            .ToListAsync(ct);
        return Results.Ok(doctors.Select(d => new PublicDoctorDto(d.Id, d.Name, d.Role, d.Description, d.SortOrder, d.ServiceIds)));
    }

    // ── Категории ───────────────────────────────────────────────────────────

    private static async Task<IResult> GetCategories(ClinicHubDbContext db, CancellationToken ct) =>
        Results.Ok(await db.ServiceCategories
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name)
            .Select(c => new CategoryDto(c.Id, c.Name, c.SortOrder, c.IsVisibleOnSite))
            .ToListAsync(ct));

    private static async Task<IResult> CreateCategory(CategoryInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return Results.BadRequest("Укажите название категории.");

        var c = new ServiceCategory { Name = dto.Name.Trim(), SortOrder = dto.SortOrder, IsVisibleOnSite = dto.IsVisibleOnSite };
        db.ServiceCategories.Add(c);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/catalog/admin/categories/{c.Id}", new CategoryDto(c.Id, c.Name, c.SortOrder, c.IsVisibleOnSite));
    }

    private static async Task<IResult> UpdateCategory(int id, CategoryInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        var c = await db.ServiceCategories.FindAsync([id], ct);
        if (c is null)
            return Results.NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name))
            return Results.BadRequest("Укажите название категории.");

        c.Name = dto.Name.Trim();
        c.SortOrder = dto.SortOrder;
        c.IsVisibleOnSite = dto.IsVisibleOnSite;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new CategoryDto(c.Id, c.Name, c.SortOrder, c.IsVisibleOnSite));
    }

    /// <summary>Услуги удалённой категории остаются, просто без категории.</summary>
    private static async Task<IResult> DeleteCategory(int id, ClinicHubDbContext db, CancellationToken ct)
    {
        var c = await db.ServiceCategories.Include(x => x.Services).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
            return Results.NotFound();

        foreach (var s in c.Services)
            s.CategoryId = null;
        db.ServiceCategories.Remove(c);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    // ── Услуги ──────────────────────────────────────────────────────────────

    private static async Task<IResult> GetServices(ClinicHubDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Services.OrderBy(s => s.SortOrder).ThenBy(s => s.Name).ToListAsync(ct)).Select(ToDto));

    private static async Task<IResult> GetService(string id, ClinicHubDbContext db, CancellationToken ct) =>
        await db.Services.FindAsync([id], ct) is { } s ? Results.Ok(ToDto(s)) : Results.NotFound();

    private static async Task<IResult> CreateService(
        ServiceInputDto dto, ClinicHubDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var id = dto.Id?.Trim().ToLowerInvariant() ?? "";
        if (!IsSlug(id))
            return Results.BadRequest("Id услуги — латиница в нижнем регистре, цифры и дефис, до 64 символов (например, consultation).");
        if (await db.Services.AnyAsync(s => s.Id == id, ct))
            return Results.Conflict($"Услуга с id «{id}» уже есть.");
        if (await ValidateServiceAsync(dto, db, ct) is { } error)
            return Results.BadRequest(error);

        var s = new Service { Id = id };
        Apply(s, dto);
        db.Services.Add(s);
        // Первая запись истории — с какой ценой услуга появилась (Old* пустые).
        db.PriceHistory.Add(new PriceHistory
        {
            ServiceId = s.Id,
            NewPriceFrom = s.PriceFrom, NewPriceTo = s.PriceTo, NewDisplay = s.PriceDisplay,
            ChangedBy = ChangedBy(user),
        });

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/catalog/admin/services/{s.Id}", ToDto(s));
    }

    private static async Task<IResult> UpdateService(
        string id, ServiceInputDto dto, ClinicHubDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        var s = await db.Services.FindAsync([id], ct);
        if (s is null)
            return Results.NotFound();
        if (await ValidateServiceAsync(dto, db, ct) is { } error)
            return Results.BadRequest(error);

        var (oldFrom, oldTo, oldDisplay) = (s.PriceFrom, s.PriceTo, s.PriceDisplay);
        Apply(s, dto);
        s.UpdatedAt = DateTime.UtcNow;
        // Каждое изменение цены или способа её показа — в историю (ADR-0007 §2). Единица — нет: это подпись, не цена.
        if (oldFrom != s.PriceFrom || oldTo != s.PriceTo || oldDisplay != s.PriceDisplay)
            db.PriceHistory.Add(new PriceHistory
            {
                ServiceId = s.Id,
                OldPriceFrom = oldFrom, NewPriceFrom = s.PriceFrom,
                OldPriceTo = oldTo, NewPriceTo = s.PriceTo,
                OldDisplay = oldDisplay, NewDisplay = s.PriceDisplay,
                ChangedBy = ChangedBy(user),
            });

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(s));
    }

    /// <summary>
    /// Удалить можно только услугу, которой не было в визитах: история визитов должна ссылаться на неё.
    /// Иначе — скрыть с сайта (IsVisibleOnSite = false).
    /// </summary>
    private static async Task<IResult> DeleteService(string id, ClinicHubDbContext db, CancellationToken ct)
    {
        var s = await db.Services.FindAsync([id], ct);
        if (s is null)
            return Results.NotFound();
        if (await db.VisitServices.AnyAsync(v => v.ServiceId == id, ct)
            || await db.ExternalServiceMaps.AnyAsync(m => m.ServiceId == id, ct))
            return Results.Conflict("Услуга уже встречается в визитах или сопоставлениях. Её нельзя удалить, но можно скрыть с сайта.");

        db.Services.Remove(s);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPriceHistory(string id, ClinicHubDbContext db, CancellationToken ct) =>
        Results.Ok(await db.PriceHistory
            .Where(p => p.ServiceId == id)
            .OrderByDescending(p => p.ChangedAt).ThenByDescending(p => p.Id)
            .Select(p => new PriceHistoryDto(
                p.Id, p.ServiceId, p.OldPriceFrom, p.NewPriceFrom, p.OldPriceTo, p.NewPriceTo,
                p.OldDisplay, p.NewDisplay, p.ChangedAt, p.ChangedBy))
            .ToListAsync(ct));

    private static async Task<string?> ValidateServiceAsync(ServiceInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return "Укажите название услуги.";
        if (!Enum.IsDefined(dto.PriceDisplay))
            return "Способ показа цены: From, Exact, Range, OnRequest или Free.";
        if (dto.PriceFrom is < 0m or > MaxPrice || dto.PriceTo is < 0m or > MaxPrice)
            return "Цена должна быть от 0 до 10 000 000 ₽.";
        if (dto.PriceFrom is decimal lo && dto.PriceTo is decimal hi && hi < lo)
            return "Цена «до» меньше цены «от».";
        if (dto.PriceUnit is { } unit)
        {
            unit = unit.Trim();
            if (unit.Length > 30)
                return "Единица цены — до 30 символов, например «за зону».";
            // Решение в #26: в единице не может быть цифр, иначе туда впишут цену текстом и она устареет.
            if (unit.Any(char.IsDigit))
                return "В единице цены не должно быть цифр (например, «за зону», «за курс»). Цена задаётся числами.";
        }
        if (dto.DurationMinutes is < 5 or > 600)
            return "Длительность — от 5 до 600 минут.";
        if (dto.CategoryId is int catId && !await db.ServiceCategories.AnyAsync(c => c.Id == catId, ct))
            return $"Категории #{catId} нет.";
        return null;
    }

    private static void Apply(Service s, ServiceInputDto dto)
    {
        s.CategoryId = dto.CategoryId;
        s.Name = dto.Name.Trim();
        s.Description = dto.Description?.Trim() ?? "";
        s.PriceFrom = dto.PriceFrom;
        s.PriceTo = dto.PriceTo;
        s.PriceDisplay = dto.PriceDisplay;
        s.PriceUnit = string.IsNullOrWhiteSpace(dto.PriceUnit) ? null : dto.PriceUnit.Trim();
        s.DurationMinutes = dto.DurationMinutes;
        s.IsVisibleOnSite = dto.IsVisibleOnSite;
        s.SortOrder = dto.SortOrder;
        s.ExternalId = string.IsNullOrWhiteSpace(dto.ExternalId) ? null : dto.ExternalId.Trim();
    }

    private static ServiceDto ToDto(Service s) => new(
        s.Id, s.CategoryId, s.Name, s.Description,
        s.PriceFrom, s.PriceTo, s.PriceDisplay, s.PriceUnit, PriceFormat.Text(s), s.DurationMinutes, s.IsVisibleOnSite, s.SortOrder,
        s.Source, s.ExternalId, s.SyncedAt, s.CreatedAt, s.UpdatedAt);

    // ── Врачи ───────────────────────────────────────────────────────────────

    private static async Task<IResult> GetDoctors(ClinicHubDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Doctors.Include(d => d.Services)
            .OrderBy(d => d.SortOrder).ThenBy(d => d.Name).ToListAsync(ct)).Select(ToDto));

    private static async Task<IResult> GetDoctor(string id, ClinicHubDbContext db, CancellationToken ct) =>
        await db.Doctors.Include(d => d.Services).FirstOrDefaultAsync(d => d.Id == id, ct) is { } d
            ? Results.Ok(ToDto(d))
            : Results.NotFound();

    private static async Task<IResult> CreateDoctor(DoctorInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        var id = dto.Id?.Trim().ToLowerInvariant() ?? "";
        if (!IsSlug(id))
            return Results.BadRequest("Id врача — латиница в нижнем регистре, цифры и дефис, до 64 символов (например, ivanova).");
        if (await db.Doctors.AnyAsync(d => d.Id == id, ct))
            return Results.Conflict($"Врач с id «{id}» уже есть.");
        if (await ValidateDoctorAsync(dto, db, ct) is { } error)
            return Results.BadRequest(error);

        var d = new Doctor { Id = id };
        Apply(d, dto);
        db.Doctors.Add(d);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/catalog/admin/doctors/{d.Id}", ToDto(d));
    }

    private static async Task<IResult> UpdateDoctor(string id, DoctorInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        var d = await db.Doctors.Include(x => x.Services).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null)
            return Results.NotFound();
        if (await ValidateDoctorAsync(dto, db, ct) is { } error)
            return Results.BadRequest(error);

        Apply(d, dto);
        d.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(d));
    }

    /// <summary>В визитах сохраняется имя врача (снимок), поэтому удаление врача историю не портит.</summary>
    private static async Task<IResult> DeleteDoctor(string id, ClinicHubDbContext db, CancellationToken ct)
    {
        var d = await db.Doctors.FindAsync([id], ct);
        if (d is null)
            return Results.NotFound();

        db.Doctors.Remove(d);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<string?> ValidateDoctorAsync(DoctorInputDto dto, ClinicHubDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return "Укажите имя врача.";
        var ids = dto.ServiceIds?.Distinct().ToList() ?? [];
        if (ids.Count > 0)
        {
            var known = await db.Services.Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
            var unknown = ids.Except(known).ToList();
            if (unknown.Count > 0)
                return "Нет таких услуг: " + string.Join(", ", unknown);
        }
        return null;
    }

    private static void Apply(Doctor d, DoctorInputDto dto)
    {
        d.Name = dto.Name.Trim();
        d.Role = dto.Role?.Trim() ?? "";
        d.Description = dto.Description?.Trim() ?? "";
        d.IsVisibleOnSite = dto.IsVisibleOnSite;
        d.SortOrder = dto.SortOrder;
        d.ExternalId = string.IsNullOrWhiteSpace(dto.ExternalId) ? null : dto.ExternalId.Trim();

        // Список услуг заменяется целиком.
        var wanted = dto.ServiceIds?.Distinct().ToHashSet() ?? [];
        d.Services.RemoveAll(x => !wanted.Contains(x.ServiceId));
        foreach (var sid in wanted.Where(sid => d.Services.All(x => x.ServiceId != sid)))
            d.Services.Add(new DoctorService { DoctorId = d.Id, ServiceId = sid });
    }

    private static DoctorDto ToDto(Doctor d) => new(
        d.Id, d.Name, d.Role, d.Description, d.IsVisibleOnSite, d.SortOrder, d.Source, d.ExternalId, d.SyncedAt,
        d.Services.Select(x => x.ServiceId).OrderBy(x => x).ToList());

    // ── Общее ───────────────────────────────────────────────────────────────

    private const decimal MaxPrice = 10_000_000m;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();

    private static bool IsSlug(string s) => s.Length is > 0 and <= 64 && SlugRegex().IsMatch(s);

    private static string ChangedBy(ClaimsPrincipal user) =>
        "user:" + (user.FindFirstValue(ClaimTypes.Name) ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "?");
}
