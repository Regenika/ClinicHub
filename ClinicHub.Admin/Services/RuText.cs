using System.Globalization;

namespace ClinicHub.Admin.Services;

/// <summary>
/// Русские названия статусов и типов, которые сервер отдаёт именами enum («Scheduled», «Mass»…).
/// Неизвестное значение показывается как есть — новый статус на сервере не ломает программу.
/// </summary>
public static class RuText
{
    public static string RequestStatus(string? value) => value switch
    {
        "Created" => "Ждёт одобрения",
        "Approved" => "Одобрена",
        "Scheduled" => "В очереди",
        "Sending" => "Отправляется",
        "Sent" => "Отправлена",
        "Partial" => "Отправлена частично",
        "Failed" => "Ошибка отправки",
        "Rejected" => "Отклонена",
        _ => value ?? "",
    };

    public static string RecipientStatus(string? value) => value switch
    {
        "Pending" => "ожидает",
        "Scheduled" => "в очереди",
        "Sending" => "отправляется",
        "Sent" => "отправлено",
        "Failed" => "ошибка",
        "Skipped" => "пропущено",
        _ => value ?? "",
    };

    public static string RequestType(string? value) => value switch
    {
        "Personal" => "личная",
        "Mass" => "массовая",
        _ => value ?? "",
    };

    public static string SendMode(string? value) => value switch
    {
        "Now" => "сразу",
        "Day" => "за сутки до приёма",
        "Hour" => "за час до приёма",
        "TwoWeeks" => "за 2 недели до приёма",
        "Custom" => "в заданное время",
        _ => value ?? "",
    };

    public static string Purpose(string? value) => value switch
    {
        "Service" => "сервисная",
        "Marketing" => "рекламная (только с согласием)",
        _ => value ?? "",
    };
}

/// <summary>Конвертер для разметки: ConverterParameter — RequestStatus, RecipientStatus, RequestType, SendMode или Purpose.</summary>
public class RuTextConverter : IValueConverter
{
    public static readonly RuTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value?.ToString();
        return (parameter as string) switch
        {
            "RequestStatus" => RuText.RequestStatus(text),
            "RecipientStatus" => RuText.RecipientStatus(text),
            "RequestType" => RuText.RequestType(text),
            "SendMode" => RuText.SendMode(text),
            "Purpose" => RuText.Purpose(text),
            _ => text ?? "",
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
