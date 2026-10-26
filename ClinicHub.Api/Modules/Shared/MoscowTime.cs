namespace ClinicHub.Api.Modules.Shared;

/// <summary>
/// Время клиники: в БД — UTC, люди и расписания — по Москве (CLAUDE.md).
/// Как и в ClinicHub.Web, с запасными вариантами для Windows без ICU.
/// </summary>
public static class MoscowTime
{
    public static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Europe/Moscow", "Russian Standard Time" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
                return zone;
        }
        return TimeZoneInfo.CreateCustomTimeZone("MSK", TimeSpan.FromHours(3), "Москва", "Москва");
    }

    /// <summary>Момент UTC → местное время по Москве (Kind = Unspecified).</summary>
    public static DateTime ToMoscow(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    /// <summary>Дата и время по Москве → момент UTC.</summary>
    public static DateTime ToUtc(DateOnly date, TimeOnly time) =>
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), Zone);

    public static DateOnly Date(DateTime utc) => DateOnly.FromDateTime(ToMoscow(utc));

    public static (DateTime FromUtc, DateTime ToUtc) DayRangeUtc(DateOnly day) =>
        (ToUtc(day, TimeOnly.MinValue), ToUtc(day.AddDays(1), TimeOnly.MinValue));
}
