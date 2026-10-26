using ClinicHub.Core.Entities;

namespace ClinicHub.Api.Modules.Messaging.Scenarios;

// Сценарии напоминаний (ADR-0008 §3–5, PLAN 1.10): рассылки сами планируются от визитов и процедур,
// администратор видит запланированное и может поправить.

/// <summary>
/// Цель сценария. От неё зависит проверка согласий (ADR-0008 §5):
/// Service — продолжение лечения по состоявшемуся визиту; Marketing — реклама, только с согласием.
/// </summary>
public enum ScenarioPurpose
{
    Service,
    Marketing,
}

public enum ScenarioTrigger
{
    /// <summary>Визит отмечен состоявшимся (с услугами из списка; пустой список — любая услуга).</summary>
    VisitCompleted,

    /// <summary>Пациент не был N дней и у него нет будущей записи.</summary>
    NoVisitFor,

    /// <summary>День рождения. Пока работает старое авто-правило (AutoRule), перенос — отдельной задачей.</summary>
    Birthday,

    /// <summary>Только вручную: администратор применяет сценарий к пациенту.</summary>
    Manual,
}

public class CareScenario
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public ScenarioPurpose Purpose { get; set; } = ScenarioPurpose.Service;
    public ScenarioTrigger Trigger { get; set; } = ScenarioTrigger.VisitCompleted;

    /// <summary>Для NoVisitFor: сколько дней без визита.</summary>
    public int? NoVisitDays { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<CareScenarioStep> Steps { get; set; } = [];

    /// <summary>Для VisitCompleted: после каких услуг срабатывает. Пусто — после любой.</summary>
    public List<CareScenarioService> TriggerServices { get; set; } = [];
}

public class CareScenarioService
{
    public int ScenarioId { get; set; }
    public CareScenario? Scenario { get; set; }
    public string ServiceId { get; set; } = "";
}

public class CareScenarioStep
{
    public int Id { get; set; }
    public int ScenarioId { get; set; }
    public CareScenario? Scenario { get; set; }

    public int Order { get; set; }

    /// <summary>Через сколько дней после события.</summary>
    public int OffsetDays { get; set; }

    /// <summary>Время отправки по Москве, в пределах 9:00–21:00.</summary>
    public TimeSpan SendTime { get; set; } = new(11, 0, 0);

    /// <summary>Текст с плейсхолдерами ({name}). По умолчанию — без диагноза и названия процедуры (ADR-0008 §5).</summary>
    public string Text { get; set; } = "";

    /// <summary>Отменить, если у пациента уже есть будущая запись.</summary>
    public bool CancelIfFutureVisit { get; set; }

    /// <summary>Отменить, если после события был любой состоявшийся визит.</summary>
    public bool CancelIfVisitAfter { get; set; }

    /// <summary>Отменить, если после события была эта услуга (например, процедуру уже повторили).</summary>
    public string? CancelIfServiceAfterId { get; set; }
}

public enum ReminderKind
{
    Scenario,
    Manual,
}

public enum ReminderStatus
{
    Planned,

    /// <summary>Передано в конвейер отправки (создана заявка RequestId).</summary>
    Sent,

    Cancelled,

    /// <summary>Не отправлено по правилам (нет согласия, отписка…), причина в StatusReason.</summary>
    Skipped,
}

/// <summary>Запланированное напоминание — видимое и редактируемое (ADR-0008 §4).</summary>
public class PlannedReminder
{
    public int Id { get; set; }

    public int PatientId { get; set; }
    public Patient? Patient { get; set; }

    public int? ScenarioId { get; set; }
    public CareScenario? Scenario { get; set; }
    public int? StepId { get; set; }
    public CareScenarioStep? Step { get; set; }

    /// <summary>Визит, от которого запланировано. Без внешнего ключа: история напоминаний важнее.</summary>
    public int? TriggerVisitId { get; set; }

    /// <summary>Момент события (UTC): с ним сравниваются условия «был визит после события».</summary>
    public DateTime EventAt { get; set; }

    public ReminderKind Kind { get; set; }
    public ScenarioPurpose Purpose { get; set; }

    /// <summary>Когда отправить, UTC.</summary>
    public DateTime DueAt { get; set; }

    public string Text { get; set; } = "";
    public ReminderStatus Status { get; set; } = ReminderStatus.Planned;
    public string? StatusReason { get; set; }

    public int? CreatedById { get; set; }

    /// <summary>Заявка на отправку, созданная из напоминания. Без внешнего ключа.</summary>
    public int? RequestId { get; set; }

    /// <summary>Когда передано в отправку (UTC). По нему считается «не больше одного напоминания в день».</summary>
    public DateTime? SentAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
