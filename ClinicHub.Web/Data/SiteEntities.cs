namespace ClinicHub.Web.Data;

/// <summary>Страница сайта (ADR-0007 §1). Содержимое — в версиях, на сайте показывается опубликованная.</summary>
public sealed class Page
{
    public int Id { get; set; }

    /// <summary>Адрес от корня: «/», «/uslugi», «/akcii/leto». Нижний регистр, без слеша на конце.</summary>
    public string Path { get; set; } = "/";

    /// <summary>Заголовок для вкладки браузера и поисковиков (title).</summary>
    public string Title { get; set; } = "";

    /// <summary>Описание для поисковиков (meta description).</summary>
    public string Description { get; set; } = "";

    /// <summary>Снятая с публикации страница отвечает 404, даже если у неё есть опубликованная версия.</summary>
    public bool IsPublished { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<PageVersion> Versions { get; set; } = [];
}

public enum PageVersionStatus
{
    Draft,
    Published,
    Archived,
}

/// <summary>
/// Версия страницы. Редактируется только черновик; при публикации черновик становится опубликованной версией,
/// а прошлая опубликованная — архивной (для отката). У страницы не больше одной опубликованной версии:
/// это следит код (частичный уникальный индекс пишется по-разному в SQL Server и PostgreSQL, ADR-0009).
/// </summary>
public sealed class PageVersion
{
    public int Id { get; set; }
    public int PageId { get; set; }
    public Page Page { get; set; } = null!;

    public PageVersionStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    /// <summary>Кто опубликовал: логин редактора или «seed» для начального наполнения.</summary>
    public string? PublishedBy { get; set; }

    public List<Block> Blocks { get; set; } = [];
}

/// <summary>Блок страницы. Тип — ключ из <see cref="Blocks.BlockTypes"/>, настройки — JSON в текстовой колонке.</summary>
public sealed class Block
{
    public int Id { get; set; }
    public int PageVersionId { get; set; }
    public PageVersion PageVersion { get; set; } = null!;

    public string Type { get; set; } = "";
    public int Order { get; set; }
    public bool IsVisible { get; set; } = true;

    /// <summary>Настройки блока, JSON. Цены и сведения об услугах сюда не кладём: их даёт каталог (ADR-0007 §2).</summary>
    public string Data { get; set; } = "{}";

    /// <summary>Фон блока из палитры сайта: пусто — как задумано в блоке, иначе ключ из <see cref="Blocks.BlockStyles"/>.</summary>
    public string Style { get; set; } = "";
}

/// <summary>Общие настройки сайта (ADR-0007 §3). В таблице одна запись.</summary>
public sealed class SiteSettings
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string LegalName { get; set; } = "";
    public string License { get; set; } = "";
    public string City { get; set; } = "";
    public string Address { get; set; } = "";
    public string Metro { get; set; } = "";
    public string Hours { get; set; } = "";
    public string Phone { get; set; } = "";

    /// <summary>Адрес сайта без слеша на конце, например https://regenika.ru. Пусто — берём из запроса.</summary>
    public string BaseUrl { get; set; } = "";

    public string TelegramUrl { get; set; } = "";
    public string MaxUrl { get; set; } = "";
    public string VkUrl { get; set; } = "";
    public string ProDoktorovUrl { get; set; } = "";
    public string ProDoktorovRating { get; set; } = "";
    public string YandexMapsUrl { get; set; } = "";
    public string YandexMapsRating { get; set; } = "";

    // Палитра сайта: «#RRGGBB» или пусто — цвет из дизайна (wwwroot/app.css). Оттенки считаются из них (Site/Theme.cs).
    public string AccentColor { get; set; } = "";
    public string BackgroundColor { get; set; } = "";
    public string TextColor { get; set; } = "";

    public DateTime UpdatedAt { get; set; }
}
