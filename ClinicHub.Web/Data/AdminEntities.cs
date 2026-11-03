namespace ClinicHub.Web.Data;

/// <summary>
/// Редактор сайта (вход в /admin). Первый этап ADR-0007 §5: локальные учётные записи в схеме site.
/// Потом — общий модуль Identity сервера с ролями Admin / Operator / Marketing (ARCHITECTURE §3).
/// </summary>
public sealed class Editor
{
    public int Id { get; set; }

    /// <summary>Логин, нижний регистр.</summary>
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Хеш PasswordHasher из ASP.NET Core Identity (PBKDF2 с солью). Сам пароль не храним.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Отключённый редактор войти не может; запись остаётся, чтобы в истории было видно, кто что менял.</summary>
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
}

/// <summary>
/// Изменение цены услуги: кто, когда, было → стало (ADR-0007 §2). Нужна для разборов «почему на сайте была другая цена».
/// Временно, вместе с каталогом в схеме site (CatalogEntities.cs); при переезде каталога в сервер — перенести.
/// </summary>
public sealed class CatalogPriceChange
{
    public int Id { get; set; }
    public int ServiceId { get; set; }
    public CatalogService Service { get; set; } = null!;

    public string OldPrice { get; set; } = "";
    public string NewPrice { get; set; } = "";
    /// <summary>Логин редактора.</summary>
    public string ChangedBy { get; set; } = "";
    public DateTime ChangedAt { get; set; }
}
