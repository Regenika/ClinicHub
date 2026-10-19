namespace ClinicHub.Api.Modules.Catalog;

// Каталог услуг, врачей и цен (ADR-0007 §2, ADR-0008 §2). Схема БД — catalog.
// Мастер-данные: сейчас ведутся в админке (Source = Manual), после выбора МИС приходят из неё (Source = Mis)
// и синхронизируются по ExternalId. Редакторские поля (описание, видимость, порядок) синхронизация не трогает.

/// <summary>Откуда пришла запись каталога.</summary>
public enum CatalogSource
{
    Manual,
    Mis,
}

/// <summary>
/// Как показывать цену (решение Свята в #26, ADR-0007 §2). Свободного текста вместо цены нет:
/// после обновления цены в МИС на сайте не должна остаться устаревшая надпись.
/// </summary>
public enum PriceDisplay
{
    /// <summary>«от N ₽» — по умолчанию. Нужна PriceFrom.</summary>
    From,

    /// <summary>«N ₽». Нужна PriceFrom.</summary>
    Exact,

    /// <summary>«от N до M ₽». Нужны PriceFrom и PriceTo.</summary>
    Range,

    /// <summary>«по запросу», числа не нужны.</summary>
    OnRequest,

    /// <summary>«бесплатно», числа не нужны.</summary>
    Free,
}

public class ServiceCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsVisibleOnSite { get; set; } = true;

    public List<Service> Services { get; set; } = [];
}

public class Service
{
    /// <summary>Латинский идентификатор (slug), идёт в адреса сайта и записи: «consultation».</summary>
    public string Id { get; set; } = "";

    public int? CategoryId { get; set; }
    public ServiceCategory? Category { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Описание для сайта (редакторское поле).</summary>
    public string Description { get; set; } = "";

    /// <summary>Цена, ₽: нижняя граница («от N») или точная («N»). Позже её будет считать синхронизация с МИС.</summary>
    public decimal? PriceFrom { get; set; }

    /// <summary>Верхняя граница, ₽ — только для PriceDisplay.Range.</summary>
    public decimal? PriceTo { get; set; }

    public PriceDisplay PriceDisplay { get; set; } = PriceDisplay.From;

    /// <summary>Единица: «за зону», «за курс». Цифр в ней быть не может, чтобы туда не вписали цену.</summary>
    public string? PriceUnit { get; set; }

    public int DurationMinutes { get; set; } = 45;
    public bool IsVisibleOnSite { get; set; } = true;
    public int SortOrder { get; set; }

    public CatalogSource Source { get; set; } = CatalogSource.Manual;

    /// <summary>Идентификатор в МИС (для синхронизации).</summary>
    public string? ExternalId { get; set; }
    public DateTime? SyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<DoctorService> Doctors { get; set; } = [];
}

public class Doctor
{
    /// <summary>Латинский идентификатор (slug): «lastochkina-shankina».</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Специальность и стаж одной строкой, как на сайте.</summary>
    public string Role { get; set; } = "";

    public string Description { get; set; } = "";
    public bool IsVisibleOnSite { get; set; } = true;
    public int SortOrder { get; set; }

    public CatalogSource Source { get; set; } = CatalogSource.Manual;
    public string? ExternalId { get; set; }
    public DateTime? SyncedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<DoctorService> Services { get; set; } = [];
}

/// <summary>Какие услуги ведёт врач.</summary>
public class DoctorService
{
    public string DoctorId { get; set; } = "";
    public Doctor? Doctor { get; set; }

    public string ServiceId { get; set; } = "";
    public Service? Service { get; set; }
}

/// <summary>
/// История цен: нужна для разборов «почему на сайте была другая цена».
/// Запись появляется при изменении PriceFrom, PriceTo или PriceDisplay; в ней обе стороны всех трёх полей.
/// </summary>
public class PriceHistory
{
    public long Id { get; set; }
    public string ServiceId { get; set; } = "";
    public decimal? OldPriceFrom { get; set; }
    public decimal? NewPriceFrom { get; set; }
    public decimal? OldPriceTo { get; set; }
    public decimal? NewPriceTo { get; set; }

    /// <summary>null — записи до этого не было (услуга создана).</summary>
    public PriceDisplay? OldDisplay { get; set; }
    public PriceDisplay NewDisplay { get; set; }
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Кто или что поменяло: «user:admin», «mis-sync», «seed».</summary>
    public string ChangedBy { get; set; } = "";
}
