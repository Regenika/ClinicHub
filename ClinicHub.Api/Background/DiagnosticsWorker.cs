using System.Text;
using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Integrations.Dikidi;
using ClinicHub.Api.Services;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Background;

/// <summary>
/// Раз в 20 секунд собирает «снимок состояния» (телефоны, SIM, очередь, заявки) и, если он изменился,
/// записывает его в журнал. По журналу видно, на каком шаге остановилась заявка и почему.
/// </summary>
public class DiagnosticsWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly EventLog _log;
    private readonly DikidiSyncState _dikidi;
    private string _last = "";

    public DiagnosticsWorker(IServiceScopeFactory scopes, EventLog log, DikidiSyncState dikidi)
    {
        _scopes = scopes;
        _log = log;
        _dikidi = dikidi;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(8), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ClinicHubDbContext>();
                var text = await BuildAsync(db, detailed: false, ct, _dikidi);
                if (text != _last)
                {
                    _last = text;
                    _log.Write("Состояние", Environment.NewLine + text);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Write("ОШИБКА/Состояние", "Не удалось собрать снимок состояния: " + ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>detailed = true добавляет «сколько секунд назад» (для страницы /api/debug/state); в журнал это не пишется, иначе строка менялась бы каждый раз.</summary>
    public static async Task<string> BuildAsync(ClinicHubDbContext db, bool detailed, CancellationToken ct, DikidiSyncState? dikidi = null)
    {
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;

        var rate = await db.RateConfigs.AsNoTracking().FirstOrDefaultAsync(c => c.IsActive, ct);
        var local = DateTime.Now.TimeOfDay;
        if (rate is null)
        {
            sb.AppendLine("Лимиты: НЕТ активной конфигурации — планировщик ничего не раздаёт.");
        }
        else
        {
            var inWindow = local >= rate.SendWindowFrom && local <= rate.SendWindowTo;
            sb.AppendLine($"Окно отправки: {rate.SendWindowFrom:hh\\:mm}–{rate.SendWindowTo:hh\\:mm}, мин. интервал {rate.MinIntervalSeconds} с. " +
                          (inWindow ? "Сейчас внутри окна." : "СЕЙЧАС ВНЕ ОКНА — SMS не раздаются."));
        }
        if (detailed)
            sb.AppendLine($"Время сервера: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var phones = await db.Phones.AsNoTracking().Include(p => p.SimCards).OrderBy(p => p.Id).ToListAsync(ct);
        sb.AppendLine($"Телефонов в системе: {phones.Count}");
        foreach (var p in phones)
        {
            var online = p.LastSeenAt != null && p.LastSeenAt.Value >= now.AddMinutes(-2);
            var seen = detailed && p.LastSeenAt != null ? $", сигнал {(int)(now - p.LastSeenAt.Value).TotalSeconds} с назад" : "";
            sb.AppendLine($"  Телефон #{p.Id} «{p.PhoneNumber}»: {(p.IsActive ? "включён" : "ОТКЛЮЧЁН")}, {(online ? "на связи" : "НЕ на связи")}{seen}");
            foreach (var s in p.SimCards)
            {
                var paused = s.PausedUntil is DateTime pu && pu > now ? $", ПАУЗА до {pu.ToLocalTime():HH:mm:ss}" : "";
                sb.AppendLine($"    SIM #{s.Id} «{s.Number}»: {(s.IsActive ? "активна" : "ВЫКЛЮЧЕНА")}, остаток {s.Balance}, " +
                              $"лимиты мин/час/сутки {s.LimitPerMinute}/{s.LimitPerHour}/{s.LimitPerDay}, " +
                              $"SubscriptionId={(s.SubscriptionId?.ToString() ?? "нет (SIM по умолчанию)")}, ошибок подряд {s.ConsecutiveFailures}{paused}");
            }
        }

        var counts = await db.SmsQueue.AsNoTracking()
            .GroupBy(q => q.Status).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct);
        sb.AppendLine("Очередь: " + (counts.Count == 0 ? "пуста" : string.Join(", ", counts.Select(c => $"{c.Key}={c.C}"))));

        var recent = await db.SmsQueue.AsNoTracking().Include(q => q.Recipient)
            .OrderByDescending(q => q.Id).Take(8).ToListAsync(ct);
        foreach (var q in recent)
        {
            sb.AppendLine($"  Задача #{q.Id}: {q.Status}, кому {q.Recipient?.Phone ?? "?"}, телефон #{q.PhoneId?.ToString() ?? "—"}, " +
                          $"SIM #{q.SimCardId?.ToString() ?? "—"}, попыток {q.Attempts}" +
                          (string.IsNullOrEmpty(q.LastError) ? "" : $", ошибка: {q.LastError}"));
        }

        var requests = await db.SmsRequests.AsNoTracking().Include(r => r.Recipients)
            .OrderByDescending(r => r.Id).Take(4).ToListAsync(ct);
        foreach (var r in requests)
        {
            var byStatus = string.Join(", ", r.Recipients.GroupBy(x => x.Status).Select(g => $"{g.Key}={g.Count()}"));
            sb.AppendLine($"  Заявка #{r.Id} «{r.Title}»: {r.Status}; получатели: {(byStatus.Length == 0 ? "нет" : byStatus)}");
        }

        if (dikidi is not null)
            sb.AppendLine(dikidi.Describe(now, detailed));

        return sb.ToString().TrimEnd();
    }
}
