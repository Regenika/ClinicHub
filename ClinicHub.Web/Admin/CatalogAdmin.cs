using System.ComponentModel.DataAnnotations;
using ClinicHub.Web.Data;
using ClinicHub.Web.Site;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Web.Admin;

/// <summary>Форма услуги в админке.</summary>
public sealed class ServiceForm
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Укажите ключ.")]
    [RegularExpression("^[a-z0-9-]{2,50}$", ErrorMessage = "Ключ: 2–50 символов, латинские строчные буквы, цифры, дефис.")]
    public string Key { get; set; } = "";

    [Required(ErrorMessage = "Укажите название."), StringLength(200, ErrorMessage = "Название — до 200 символов.")]
    public string Name { get; set; } = "";

    [StringLength(1000, ErrorMessage = "Описание — до 1000 символов.")]
    public string? Text { get; set; }

    [Required(ErrorMessage = "Укажите цену, например «от 3 000 ₽»."), StringLength(100, ErrorMessage = "Цена — до 100 символов.")]
    public string Price { get; set; } = "";

    [Range(15, 240, ErrorMessage = "Длительность — от 15 до 240 минут.")]
    public int DurationMinutes { get; set; } = 45;

    public bool IsVisible { get; set; } = true;
}

public sealed class DoctorForm
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Укажите ключ.")]
    [RegularExpression("^[a-z0-9-]{2,50}$", ErrorMessage = "Ключ: 2–50 символов, латинские строчные буквы, цифры, дефис.")]
    public string Key { get; set; } = "";

    [Required(ErrorMessage = "Укажите ФИО."), StringLength(200, ErrorMessage = "ФИО — до 200 символов.")]
    public string Name { get; set; } = "";

    [StringLength(300, ErrorMessage = "Специальность — до 300 символов.")]
    public string? Role { get; set; }

    public bool IsVisible { get; set; } = true;

    /// <summary>Id услуг, которые ведёт врач. Пусто — все.</summary>
    public HashSet<int> ServiceIds { get; set; } = [];
}

public sealed record ServiceRow(int Id, string Key, string Name, string Price, int DurationMinutes, bool IsVisible, int Order);
public sealed record DoctorRow(int Id, string Key, string Name, string Role, bool IsVisible, IReadOnlyList<string> ServiceNames);
public sealed record PriceChangeRow(string Service, string OldPrice, string NewPrice, string ChangedBy, DateTime ChangedAt);

/// <summary>
/// Правка временного каталога (site.Catalog*) и настроек сайта из админки. После сохранения сбрасывает кэши,
/// поэтому на этом компьютере изменение видно сразу, на остальных, подключённых к той же базе, — в течение 30 секунд.
/// Когда каталог переедет в сервер (Issue #13), каталожная часть станет клиентом его API.
/// </summary>
public sealed class CatalogAdmin(IDbContextFactory<SiteDbContext> dbFactory, SiteCache siteCache, DbSiteCatalog catalog, TimeProvider clock)
{
    // ── Услуги ──────────────────────────────────────────────────────────────

    public async Task<List<ServiceRow>> ServicesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CatalogServices.AsNoTracking()
            .OrderBy(s => s.Order).ThenBy(s => s.Id)
            .Select(s => new ServiceRow(s.Id, s.Key, s.Name, s.Price, s.DurationMinutes, s.IsVisible, s.Order))
            .ToListAsync();
    }

    public async Task<ServiceForm?> ServiceAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CatalogServices.AsNoTracking().Where(s => s.Id == id)
            .Select(s => new ServiceForm
            {
                Id = s.Id, Key = s.Key, Name = s.Name, Text = s.Text, Price = s.Price,
                DurationMinutes = s.DurationMinutes, IsVisible = s.IsVisible,
            })
            .FirstOrDefaultAsync();
    }

    /// <summary>Сохранить услугу. Изменение цены пишется в историю. Возвращает ошибку или null.</summary>
    public async Task<string?> SaveServiceAsync(ServiceForm f, string editor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var now = clock.GetUtcNow().UtcDateTime;
        var key = f.Key.Trim();
        if (await db.CatalogServices.AnyAsync(s => s.Key == key && s.Id != f.Id))
            return "Услуга с таким ключом уже есть.";

        CatalogService s;
        if (f.Id == 0)
        {
            var maxOrder = await db.CatalogServices.MaxAsync(x => (int?)x.Order) ?? 0;
            s = new CatalogService { Key = key, Order = maxOrder + 10 };
            db.CatalogServices.Add(s);
        }
        else
        {
            s = await db.CatalogServices.FirstAsync(x => x.Id == f.Id);
            // Ключ стоит в адресах записи (/zapis?service=…) и в блоках страниц: менять его нельзя.
            if (s.Key != key)
                return "Ключ существующей услуги менять нельзя: на него ссылаются страницы и адреса записи.";
            if (s.Price != f.Price.Trim())
                db.CatalogPriceChanges.Add(new CatalogPriceChange { Service = s, OldPrice = s.Price, NewPrice = f.Price.Trim(), ChangedBy = editor, ChangedAt = now });
        }

        s.Name = f.Name.Trim();
        s.Text = f.Text?.Trim() ?? "";
        s.Price = f.Price.Trim();
        s.DurationMinutes = f.DurationMinutes;
        s.IsVisible = f.IsVisible;
        s.UpdatedAt = now;
        await db.SaveChangesAsync();
        Changed();
        return null;
    }

    /// <summary>Сдвинуть услугу выше (-1) или ниже (+1) в списке на сайте.</summary>
    public async Task MoveServiceAsync(int id, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.CatalogServices.OrderBy(s => s.Order).ThenBy(s => s.Id).ToListAsync();
        Move(list, id, direction, (s, order) => s.Order = order, s => s.Id);
        await db.SaveChangesAsync();
        Changed();
    }

    public async Task DeleteServiceAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CatalogServices.Where(s => s.Id == id).ExecuteDeleteAsync();
        Changed();
    }

    public async Task<List<PriceChangeRow>> PriceHistoryAsync(int take = 50)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.CatalogPriceChanges.AsNoTracking()
            .OrderByDescending(c => c.ChangedAt).Take(take)
            .Select(c => new PriceChangeRow(c.Service.Name, c.OldPrice, c.NewPrice, c.ChangedBy, c.ChangedAt))
            .ToListAsync();
    }

    // ── Врачи ───────────────────────────────────────────────────────────────

    public async Task<List<DoctorRow>> DoctorsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.CatalogDoctors.AsNoTracking()
            .OrderBy(d => d.Order).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Key, d.Name, d.Role, d.IsVisible, Services = d.Services.Select(x => x.Service.Name).ToList() })
            .ToListAsync();
        return rows.Select(d => new DoctorRow(d.Id, d.Key, d.Name, d.Role, d.IsVisible, d.Services)).ToList();
    }

    public async Task<DoctorForm?> DoctorAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var d = await db.CatalogDoctors.AsNoTracking().Include(x => x.Services).FirstOrDefaultAsync(x => x.Id == id);
        return d is null ? null : new DoctorForm
        {
            Id = d.Id, Key = d.Key, Name = d.Name, Role = d.Role, IsVisible = d.IsVisible,
            ServiceIds = d.Services.Select(x => x.ServiceId).ToHashSet(),
        };
    }

    public async Task<string?> SaveDoctorAsync(DoctorForm f)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var key = f.Key.Trim();
        if (await db.CatalogDoctors.AnyAsync(d => d.Key == key && d.Id != f.Id))
            return "Врач с таким ключом уже есть.";

        CatalogDoctor d;
        if (f.Id == 0)
        {
            var maxOrder = await db.CatalogDoctors.MaxAsync(x => (int?)x.Order) ?? 0;
            d = new CatalogDoctor { Key = key, Order = maxOrder + 10 };
            db.CatalogDoctors.Add(d);
        }
        else
        {
            d = await db.CatalogDoctors.Include(x => x.Services).FirstAsync(x => x.Id == f.Id);
            if (d.Key != key)
                return "Ключ существующего врача менять нельзя: на него ссылаются страницы и адреса записи.";
        }

        d.Name = f.Name.Trim();
        d.Role = f.Role?.Trim() ?? "";
        d.IsVisible = f.IsVisible;
        d.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        d.Services.RemoveAll(x => !f.ServiceIds.Contains(x.ServiceId));
        foreach (var id in f.ServiceIds.Where(id => d.Services.All(x => x.ServiceId != id)))
            d.Services.Add(new CatalogDoctorService { ServiceId = id });
        await db.SaveChangesAsync();
        Changed();
        return null;
    }

    public async Task MoveDoctorAsync(int id, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var list = await db.CatalogDoctors.OrderBy(d => d.Order).ThenBy(d => d.Id).ToListAsync();
        Move(list, id, direction, (d, order) => d.Order = order, d => d.Id);
        await db.SaveChangesAsync();
        Changed();
    }

    public async Task DeleteDoctorAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.CatalogDoctors.Where(d => d.Id == id).ExecuteDeleteAsync();
        Changed();
    }

    // ── Настройки сайта ─────────────────────────────────────────────────────

    public async Task<SiteSettings> SettingsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Settings.AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync() ?? new SiteSettings();
    }

    public async Task SaveSettingsAsync(SiteSettings f)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.Settings.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (s is null)
        {
            s = new SiteSettings();
            db.Settings.Add(s);
        }
        f.Id = s.Id;
        db.Entry(s).CurrentValues.SetValues(f);
        s.BaseUrl = s.BaseUrl.Trim().TrimEnd('/');
        s.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        Changed();
    }

    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Порядок 10, 20, 30… с перестановкой одного элемента на шаг вверх или вниз.</summary>
    private static void Move<T>(List<T> list, int id, int direction, Action<T, int> setOrder, Func<T, int> idOf)
    {
        var i = list.FindIndex(x => idOf(x) == id);
        var j = i + direction;
        if (i >= 0 && j >= 0 && j < list.Count)
            (list[i], list[j]) = (list[j], list[i]);
        for (var k = 0; k < list.Count; k++)
            setOrder(list[k], (k + 1) * 10);
    }

    private void Changed()
    {
        siteCache.Clear();
        catalog.Invalidate();
    }
}
