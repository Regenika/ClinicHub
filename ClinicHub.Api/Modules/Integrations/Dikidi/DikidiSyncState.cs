namespace ClinicHub.Api.Modules.Integrations.Dikidi;

/// <summary>Итог одной синхронизации. Только счётчики и названия услуг — без данных пациентов.</summary>
public sealed record DikidiSyncResult(
    DateOnly From,
    DateOnly To,
    int Records,
    int Created,
    int Updated,
    int Cancelled,
    int PatientsCreated,
    int SkippedNoPhone,
    int SkippedBlocks,
    int Untouched,
    IReadOnlyList<UnmappedDikidiService> Unmapped);

/// <summary>Название услуги из Dikidi, которого нет в сопоставлении с каталогом, и в скольких записях оно встретилось.</summary>
public sealed record UnmappedDikidiService(string Name, int Records);

/// <summary>
/// Состояние адаптера в памяти сервера: для /api/debug/state и API админки. После перезапуска — пусто
/// до первой синхронизации (через минуту после старта).
/// </summary>
public sealed class DikidiSyncState
{
    private readonly object _lock = new();

    /// <summary>Не даёт фоновой и ручной синхронизации идти одновременно.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public bool Configured { get; set; }
    public DateTime? LastAttemptAt { get; private set; }
    public DateTime? LastSuccessAt { get; private set; }
    public DateTime? LastErrorAt { get; private set; }
    public string? LastError { get; private set; }
    public DikidiSyncResult? LastResult { get; private set; }

    public void Started(DateTime nowUtc)
    {
        lock (_lock) LastAttemptAt = nowUtc;
    }

    public void Succeeded(DateTime nowUtc, DikidiSyncResult result)
    {
        lock (_lock)
        {
            LastSuccessAt = nowUtc;
            LastResult = result;
            LastError = null;
            LastErrorAt = null;
        }
    }

    public void Failed(DateTime nowUtc, string error)
    {
        lock (_lock)
        {
            LastErrorAt = nowUtc;
            LastError = error;
        }
    }

    /// <summary>
    /// Строки для снимка состояния. Без detailed — только то, что меняется редко (снимок пишется в журнал
    /// при каждом изменении, а время успешной синхронизации менялось бы каждые 15 минут).
    /// </summary>
    public string Describe(DateTime nowUtc, bool detailed)
    {
        lock (_lock)
        {
            if (!Configured)
                return "Dikidi: адаптер выключен (не заданы Dikidi:Phone, Dikidi:Password, Dikidi:CompanyId).";

            var status = LastError is not null
                ? $"Dikidi: ОШИБКА — {LastError}"
                : LastSuccessAt is null ? "Dikidi: ещё не синхронизировались." : "Dikidi: работает.";
            if (!detailed)
                return status;

            var lines = new List<string> { status };
            lines.Add(LastSuccessAt is DateTime ok
                ? $"  Последняя удачная синхронизация: {ok.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({Ago(nowUtc - ok)} назад)"
                : "  Удачных синхронизаций с запуска сервера не было.");
            if (LastErrorAt is DateTime err)
                lines.Add($"  Ошибка: {err.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            if (LastResult is { } r)
            {
                lines.Add($"  Период {r.From:dd.MM}–{r.To:dd.MM}: записей {r.Records}, новых визитов {r.Created}, изменено {r.Updated}, " +
                          $"отменено {r.Cancelled}, новых пациентов {r.PatientsCreated}, без телефона {r.SkippedNoPhone}, " +
                          $"служебных блокировок {r.SkippedBlocks}, уже подтверждено администратором {r.Untouched}");
                if (r.Unmapped.Count > 0)
                    lines.Add($"  Несопоставленные услуги ({r.Unmapped.Count}): " +
                              string.Join(", ", r.Unmapped.Take(10).Select(u => $"«{u.Name}»")) +
                              (r.Unmapped.Count > 10 ? " …" : "") + " — сопоставьте в /api/visits/service-map");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{(int)t.TotalSeconds} с" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} мин" : $"{t.TotalHours:0.#} ч";
}
