namespace ClinicHub.Web.Data;

// ВРЕМЕННЫЙ каталог в схеме site (решение Свята 2026-10-17): по ADR-0008 §2 каталог принадлежит серверу
// (модуль Catalog в ClinicHub.Api, Issue #13). Пока его нет, услуги и врачи живут здесь, чтобы на демо-компьютерах
// цены правились в одной общей базе, а не в site-content.json на каждом. Когда появится API каталога —
// перенести данные в схему catalog, таблицы Catalog* удалить миграцией, DbSiteCatalog заменить клиентом API.

/// <summary>Услуга на сайте и в записи.</summary>
public sealed class CatalogService
{
    public int Id { get; set; }

    /// <summary>Латинский ключ, идёт в адрес записи (/zapis?service=prp) и в выбор блоков. Менять нельзя: сломаются ссылки.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Описание для сайта.</summary>
    public string Text { get; set; } = "";
    /// <summary>Цена как текст: «от 3 000 ₽». Числовая цена «от» и история цен — в каталоге сервера (ADR-0007 §2).</summary>
    public string Price { get; set; } = "";
    public int DurationMinutes { get; set; } = 45;

    public bool IsVisible { get; set; } = true;
    public int Order { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CatalogDoctor
{
    public int Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Специальность и стаж одной строкой.</summary>
    public string Role { get; set; } = "";
    /// <summary>Фото из медиатеки. Удалили картинку — фото пропадает, на сайте снова место для фото.</summary>
    public int? PhotoId { get; set; }

    public bool IsVisible { get; set; } = true;
    public int Order { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Услуги, которые ведёт врач. Ни одной — ведёт все (как было в site-content.json).</summary>
    public List<CatalogDoctorService> Services { get; set; } = [];
}

public sealed class CatalogDoctorService
{
    public int DoctorId { get; set; }
    public CatalogDoctor Doctor { get; set; } = null!;
    public int ServiceId { get; set; }
    public CatalogService Service { get; set; } = null!;
}
