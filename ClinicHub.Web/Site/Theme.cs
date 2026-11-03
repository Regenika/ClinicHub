using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClinicHub.Web.Site;

/// <summary>
/// Палитра сайта из настроек: основной цвет, фон, текст. Остальные оттенки (тёмный для наведения, светлый для фона карточек,
/// приглушённый текст) считаются из них, поэтому клиника меняет три цвета, а дизайн остаётся согласованным.
/// Пустое значение — цвет из дизайна (wwwroot/app.css).
/// </summary>
public static partial class Theme
{
    public const string DefaultAccent = "#2E6A5A";
    public const string DefaultBackground = "#F1F3F2";
    public const string DefaultText = "#18211E";

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexPattern();

    public static bool IsValid(string? hex) => string.IsNullOrEmpty(hex) || HexPattern().IsMatch(hex);

    /// <summary>CSS с переопределёнными переменными или пустая строка, если палитра не менялась.</summary>
    public static string Css(ClinicInfo c)
    {
        if (string.IsNullOrEmpty(c.AccentColor) && string.IsNullOrEmpty(c.BackgroundColor) && string.IsNullOrEmpty(c.TextColor))
            return "";
        var sb = new StringBuilder(":root{");
        if (Parse(c.AccentColor) is { } accent)
        {
            sb.Append($"--accent:{Hex(accent)};");
            sb.Append($"--accent-dark:{Hex(Mix(accent, (0, 0, 0), 0.3))};");
            sb.Append($"--accent-soft:{Hex(Mix(accent, (255, 255, 255), 0.85))};");
        }
        var bg = Parse(c.BackgroundColor);
        var ink = Parse(c.TextColor);
        if (bg is { } b)
        {
            sb.Append($"--ground:{Hex(b)};");
            sb.Append($"--surface-soft:{Hex(Mix(b, (255, 255, 255), 0.6))};");
        }
        if (ink is { } i)
        {
            var against = bg ?? Parse(DefaultBackground)!.Value;
            sb.Append($"--ink:{Hex(i)};--dark:{Hex(i)};");
            sb.Append($"--ink-muted-strong:{Hex(Mix(i, against, 0.22))};");
            sb.Append($"--ink-muted:{Hex(Mix(i, against, 0.35))};");
        }
        return sb.Append('}').ToString();
    }

    /// <summary>Предупреждения о контрасте (WCAG 2.1, нужно не меньше 4,5:1 для обычного текста). Пусто — всё читается.</summary>
    public static List<string> ContrastWarnings(string? accent, string? background, string? text)
    {
        var a = Parse(accent) ?? Parse(DefaultAccent)!.Value;
        var b = Parse(background) ?? Parse(DefaultBackground)!.Value;
        var t = Parse(text) ?? Parse(DefaultText)!.Value;
        var warnings = new List<string>();
        Check(warnings, t, b, "Текст на фоне страницы");
        Check(warnings, t, (255, 255, 255), "Текст на белых карточках");
        Check(warnings, (255, 255, 255), a, "Белый текст на кнопках основного цвета");
        Check(warnings, a, b, "Ссылки основного цвета на фоне страницы");
        return warnings;
    }

    private static void Check(List<string> warnings, (int, int, int) fg, (int, int, int) bg, string what)
    {
        var ratio = Contrast(fg, bg);
        if (ratio < 4.5)
            warnings.Add($"{what}: контраст {ratio.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU"))}:1, нужно не меньше 4,5:1 — будет плохо читаться.");
    }

    public static (int R, int G, int B)? Parse(string? hex) =>
        string.IsNullOrEmpty(hex) || !HexPattern().IsMatch(hex)
            ? null
            : (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

    private static string Hex((int R, int G, int B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Смесь двух цветов: amount = доля второго.</summary>
    private static (int, int, int) Mix((int R, int G, int B) a, (int R, int G, int B) b, double amount) =>
        ((int)Math.Round(a.R + (b.R - a.R) * amount), (int)Math.Round(a.G + (b.G - a.G) * amount), (int)Math.Round(a.B + (b.B - a.B) * amount));

    private static double Contrast((int, int, int) a, (int, int, int) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
