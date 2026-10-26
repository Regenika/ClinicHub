using ClinicHub.Web.Site;

namespace ClinicHub.Web.Booking;

/// <summary>
/// Демо-источник слотов, пока нет МИС. Генерирует правдоподобное расписание:
/// пн–сб, 10:00–19:00 по Москве, шаг равен длительности услуги, часть окон «занята».
/// Расписание детерминированное, поэтому одно и то же время не пропадает между шагами записи.
/// </summary>
public sealed class DemoBookingSource(ISiteCatalog catalog, TimeProvider clock) : IBookingSource
{
    private static readonly TimeOnly DayStart = new(10, 0);
    private static readonly TimeOnly DayEnd = new(19, 0);

    public bool IsDemo => true;

    public Task<IReadOnlyList<BookingSlot>> GetSlotsAsync(
        string serviceId, string? doctorId, DateOnly date, CancellationToken ct = default)
    {
        var service = catalog.Services.FirstOrDefault(s => s.Id == serviceId);
        if (service is null || date.DayOfWeek == DayOfWeek.Sunday)
            return Task.FromResult<IReadOnlyList<BookingSlot>>([]);

        var doctors = catalog.Doctors
            .Where(d => d.Treats(serviceId))
            .Where(d => doctorId is null || d.Id == doctorId)
            .ToList();

        var now = clock.GetUtcNow();
        var step = TimeSpan.FromMinutes(Math.Clamp(service.DurationMinutes, 15, 240));
        var slots = new List<BookingSlot>();

        foreach (var doctor in doctors)
        {
            for (var t = DayStart; t.Add(step) <= DayEnd && t >= DayStart; t = t.Add(step))
            {
                var start = ClinicTime.FromLocal(date, t);
                if (start <= now.AddHours(1))
                    continue;
                if (IsBusy(doctor.Id, date, t))
                    continue;
                slots.Add(new BookingSlot(start, doctor.Id));
            }
        }

        // Для «любого врача» на одно и то же время оставляем одно окно.
        IReadOnlyList<BookingSlot> result = slots
            .GroupBy(s => s.StartUtc)
            .Select(g => g.First())
            .OrderBy(s => s.StartUtc)
            .ToList();
        return Task.FromResult(result);
    }

    /// <summary>Примерно 40 % окон заняты. Хеш стабилен между запусками (не string.GetHashCode).</summary>
    private static bool IsBusy(string doctorId, DateOnly date, TimeOnly time)
    {
        var key = $"{doctorId}|{date:yyyyMMdd}|{time:HHmm}";
        uint h = 2166136261;
        foreach (var c in key)
            h = (h ^ c) * 16777619;
        return h % 10 < 4;
    }
}
