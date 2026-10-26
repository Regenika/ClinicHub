using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Shared;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Messaging.Scenarios;

/// <summary>
/// Окно отправки по Москве — одно на все отправки (решение Свята в #31): часы берутся из активной
/// <c>RateConfig</c>, той же, по которой планировщик раздаёт SMS на SIM. Если клиника поменяет часы,
/// они сразу действуют и для напоминаний, и для проверки времени шагов.
/// </summary>
public readonly record struct SendWindow(TimeSpan From, TimeSpan To)
{
    /// <summary>Значения <c>RateConfig</c> по умолчанию — на случай, если активной конфигурации нет.</summary>
    public static readonly SendWindow Default = new(new TimeSpan(9, 0, 0), new TimeSpan(20, 0, 0));

    public static async Task<SendWindow> LoadAsync(ClinicHubDbContext db, CancellationToken ct)
    {
        var rate = await db.RateConfigs.AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.Id)
            .Select(c => new { c.SendWindowFrom, c.SendWindowTo })
            .FirstOrDefaultAsync(ct);
        return rate is null || rate.SendWindowFrom >= rate.SendWindowTo
            ? Default
            : new SendWindow(rate.SendWindowFrom, rate.SendWindowTo);
    }

    /// <summary>Открыто ли окно в этот момент. Границы — как у планировщика SMS: [From; To] включительно.</summary>
    public bool IsOpenAt(DateTime utc)
    {
        var t = MoscowTime.ToMoscow(utc).TimeOfDay;
        return t >= From && t <= To;
    }

    /// <summary>
    /// Можно ли назначить на это время шаг или ручное напоминание. Ровно To не берём: воркер просыпается раз
    /// в минуту, и в 20:00:30 окно уже закрыто — напоминание молча ушло бы на утро.
    /// </summary>
    public bool Allows(TimeOnly time) => time.ToTimeSpan() >= From && time.ToTimeSpan() < To;

    public override string ToString() => $"{From:h\\:mm}–{To:h\\:mm}";
}
