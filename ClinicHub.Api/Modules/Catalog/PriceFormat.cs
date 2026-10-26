using System.Globalization;

namespace ClinicHub.Api.Modules.Catalog;

/// <summary>
/// Текст цены для показа людям. Одни правила для сайта, ботов и рассылок (решение в #26, ADR-0007 §2).
/// Если для выбранного способа нет нужного числа — «по запросу», а не пустота и не «от 0 ₽».
/// </summary>
public static class PriceFormat
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Text(PriceDisplay display, decimal? from, decimal? to, string? unit)
    {
        var text = display switch
        {
            PriceDisplay.Free => "бесплатно",
            PriceDisplay.OnRequest => null,
            PriceDisplay.Exact when from is decimal f => $"{Money(f)} ₽",
            PriceDisplay.Range when from is decimal f && to is decimal t => $"от {Money(f)} до {Money(t)} ₽",
            PriceDisplay.From when from is decimal f => $"от {Money(f)} ₽",
            _ => null,
        };
        if (text is null)
            return "по запросу";
        // Единица имеет смысл только рядом с числом: «бесплатно за зону» не пишем.
        return display != PriceDisplay.Free && !string.IsNullOrWhiteSpace(unit) ? $"{text} {unit.Trim()}" : text;
    }

    public static string Text(Service s) => Text(s.PriceDisplay, s.PriceFrom, s.PriceTo, s.PriceUnit);

    /// <summary>«2 500», «2 500,50» — разряды неразрывным пробелом, копейки только если есть.</summary>
    private static string Money(decimal value) => value.ToString("#,0.##", Ru);
}
