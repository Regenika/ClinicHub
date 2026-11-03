namespace ClinicHub.Web.Blocks;

/// <summary>
/// Фон блока — вариант из палитры сайта, а не произвольный цвет: так текст внутри всегда остаётся читаемым,
/// а смена основного цвета в настройках перекрашивает и блоки. Стили — в wwwroot/app.css (.block-bg--…).
/// </summary>
public static class BlockStyles
{
    public static readonly IReadOnlyList<(string Key, string Title)> All =
    [
        ("", "Как задумано в блоке"),
        ("ground", "Фон страницы"),
        ("light", "Белый"),
        ("soft", "Светлый основного цвета"),
        ("accent", "Основной цвет"),
        ("dark", "Тёмный"),
    ];

    public static string Normalize(string? key) => All.Any(s => s.Key == key) ? key! : "";

    public static string Title(string? key) => All.FirstOrDefault(s => s.Key == (key ?? "")).Title ?? "";
}

/// <summary>Поле int? с id картинки из медиатеки: в редакторе блока вместо числа — выбор картинки с превью.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MediaImageAttribute : Attribute;
