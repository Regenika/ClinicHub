namespace ClinicHub.Api.Modules.Integrations.Dikidi;

// Адаптер Dikidi — только чтение журнала записей (ADR-0004, ADR-0008 §1, Issue #15).
// Официального API у Dikidi нет: повторяем запросы веб-кабинета. Подробности и риски — в Issue #15 и README адаптера.

/// <summary>
/// Настройки из секции «Dikidi». Логин и пароль — только user-secrets или переменные окружения
/// (Dikidi__Phone, Dikidi__Password, Dikidi__CompanyId), никогда в git. Без них адаптер выключен.
/// </summary>
public sealed class DikidiOptions
{
    /// <summary>Номер телефона отдельной учётки сотрудника с минимальными правами (ADR-0004).</summary>
    public string Phone { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>ID компании в Dikidi (число из адреса журнала: …/owner/journal/?company=123456).</summary>
    public string CompanyId { get; set; } = "";

    /// <summary>Как часто забирать журнал. Реже 5 минут не бывает: частые запросы — риск блокировки учётки.</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>Сколько дней назад смотреть (вчерашние записи могли отменить задним числом).</summary>
    public int DaysBack { get; set; } = 1;

    public int DaysAhead { get; set; } = 30;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Phone) && !string.IsNullOrWhiteSpace(Password) && !string.IsNullOrWhiteSpace(CompanyId);

    public TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(5, IntervalMinutes));
}

public enum DikidiRecordState
{
    Active,
    /// <summary>Отменена или удалена в Dikidi (status_id 4 или 5).</summary>
    Cancelled,
}

/// <summary>Запись журнала Dikidi в том виде, который нужен ClinicHub. Время — по Москве, как в журнале клиники.</summary>
public sealed record DikidiRecord(
    string Id,
    string MasterId,
    string MasterName,
    DateOnly Date,
    TimeOnly Start,
    string? ClientName,
    string? ClientPhone,
    IReadOnlyList<string> Services,
    DikidiRecordState State);

/// <summary>Ответ журнала: записи пациентов и сколько служебных блокировок (перерывы, личное время) пропущено.</summary>
public sealed record DikidiJournal(IReadOnlyList<DikidiRecord> Records, int SkippedBlocks);

/// <summary>
/// Понятная ошибка адаптера. Текст уходит в журнал сервера и в /api/debug/state,
/// поэтому в нём никогда нет данных пациентов и тел ответов Dikidi.
/// </summary>
public sealed class DikidiException(string message) : Exception(message);
