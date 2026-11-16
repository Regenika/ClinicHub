using ClinicHub.Web.Booking;

namespace ClinicHub.Web.Admin;

/// <summary>Тексты и форматы панели рассылок: статусы человеческими словами, время по Москве, подсчёт SMS.</summary>
public static class MailingText
{
    /// <summary>Подпись в конце рекламы: без неё пациенту нечем отказаться (закон «О рекламе», ст. 18).</summary>
    public const string StopSuffix = " Отказ: СТОП";

    public static string When(DateTime? utc)
    {
        if (utc is not DateTime u)
            return "—";
        var local = ClinicTime.ToLocal(new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc)));
        var today = ClinicTime.ToLocal(DateTimeOffset.UtcNow).Date;
        return local.Date == today ? local.ToString("HH:mm", ClinicTime.Ru)
            : local.Date == today.AddDays(1) ? "завтра " + local.ToString("HH:mm", ClinicTime.Ru)
            : local.Date == today.AddDays(-1) ? "вчера " + local.ToString("HH:mm", ClinicTime.Ru)
            : local.ToString("d MMMM HH:mm", ClinicTime.Ru);
    }

    public static string Day(DateTime? utc) => utc is DateTime u
        ? ClinicTime.ToLocal(new DateTimeOffset(DateTime.SpecifyKind(u, DateTimeKind.Utc))).ToString("dd.MM.yyyy", ClinicTime.Ru)
        : "—";

    /// <summary>Статус рассылки: (текст, класс плашки).</summary>
    public static (string Text, string Css) RequestStatus(string status) => status switch
    {
        "Created" or "Approved" or "Scheduled" => ("Ждёт отправки", "m-chip--wait"),
        "Sending" => ("Идёт", "m-chip--ok"),
        "Paused" => ("На паузе", "m-chip--wait"),
        "Sent" => ("Завершена", ""),
        "Partial" => ("Завершена частично", ""),
        "Failed" => ("Не ушла", "m-chip--bad"),
        "Rejected" => ("Остановлена", ""),
        _ => (status, ""),
    };

    public static bool IsRunning(string status) => status is "Created" or "Approved" or "Scheduled" or "Sending";

    /// <summary>Статус одного сообщения в журнале.</summary>
    public static (string Text, string Css) RecipientStatus(string status) => status switch
    {
        "Sent" => ("Отправлено", "m-chip--ok"),
        "Failed" => ("Не ушло", "m-chip--bad"),
        "Skipped" => ("Пропущено", ""),
        "Pending" => ("Ждёт", "m-chip--wait"),
        "Scheduled" or "Sending" => ("В очереди", "m-chip--wait"),
        _ => (status, ""),
    };

    public static string Purpose(string purpose) => purpose == "Marketing" ? "реклама" : "по записи";

    /// <summary>Как телефон посчитает SMS: кириллица — 70 знаков (67 в длинном), латиница — 160 (153).</summary>
    public static (int Parts, int Length, int Left) Segments(string text)
    {
        var unicode = text.Any(c => c > 127);
        int one = unicode ? 70 : 160, part = unicode ? 67 : 153;
        if (text.Length == 0)
            return (0, 0, one);
        var parts = text.Length <= one ? 1 : (int)Math.Ceiling(text.Length / (double)part);
        var capacity = parts == 1 ? one : parts * part;
        return (parts, text.Length, capacity - text.Length);
    }

    public static string Counter(string text)
    {
        var (parts, length, left) = Segments(text);
        return parts switch
        {
            0 => "Текст пустой",
            1 => $"1 SMS · {length} знаков, до второй SMS ещё {left}",
            _ => $"{parts} SMS · {length} знаков. Длинное сообщение стоит как {parts} SMS",
        };
    }

    /// <summary>Как увидит пациент: {Имя} → пример имени.</summary>
    public static string Preview(string text, string name, bool marketing) =>
        (text.Replace("{Имя}", name, StringComparison.OrdinalIgnoreCase).Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
         + (marketing && text.Length > 0 && !text.Contains("СТОП", StringComparison.OrdinalIgnoreCase) ? StopSuffix : "")).Trim();

    /// <summary>Текст для отправки: метка {Имя} понятна серверу, «Отказ: СТОП» — в рекламе обязательно.</summary>
    public static string ForSend(string text, bool marketing) =>
        (text.Trim() + (marketing && !text.Contains("СТОП", StringComparison.OrdinalIgnoreCase) ? StopSuffix : "")).Trim();

    public static string Plural(int n, string one, string few, string many)
    {
        var mod10 = n % 10;
        var mod100 = n % 100;
        return mod10 == 1 && mod100 != 11 ? one
            : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? few
            : many;
    }
}
