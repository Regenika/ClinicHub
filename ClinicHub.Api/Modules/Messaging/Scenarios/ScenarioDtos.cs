namespace ClinicHub.Api.Modules.Messaging.Scenarios;

/// <summary>Шаг сценария. SendTime — время по Москве «ЧЧ:ММ», от 09:00 до 20:59.</summary>
public record ScenarioStepDto(
    int? Id, int Order, int OffsetDays, string SendTime, string Text,
    bool CancelIfFutureVisit = false, bool CancelIfVisitAfter = false, string? CancelIfServiceAfterId = null);

public record ScenarioDto(
    int Id, string Name, bool IsActive, ScenarioPurpose Purpose, ScenarioTrigger Trigger, int? NoVisitDays,
    IReadOnlyList<string> TriggerServiceIds, IReadOnlyList<ScenarioStepDto> Steps, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Создание и правка сценария. Шаги и список услуг заменяются целиком.</summary>
public record ScenarioInputDto(
    string Name, ScenarioPurpose Purpose, ScenarioTrigger Trigger, IReadOnlyList<ScenarioStepDto>? Steps,
    IReadOnlyList<string>? TriggerServiceIds = null, int? NoVisitDays = null, bool IsActive = true);

public record ReminderDto(
    int Id, int PatientId, string? PatientName, int? ScenarioId, string? ScenarioName, int? StepId, int? TriggerVisitId,
    ReminderKind Kind, ScenarioPurpose Purpose, DateTime DueAt, string Text, ReminderStatus Status, string? StatusReason,
    int? RequestId, DateTime? SentAt, DateTime CreatedAt);

/// <summary>Ручное напоминание: либо через InDays дней в Time (по Москве, по умолчанию 11:00), либо точный DueAt.</summary>
public record ManualReminderDto(
    int PatientId, string Text, int? InDays = null, string? Time = null, DateTime? DueAt = null,
    ScenarioPurpose Purpose = ScenarioPurpose.Service);

/// <summary>Правка запланированного напоминания: новое время и/или текст.</summary>
public record ReminderUpdateDto(DateTime? DueAt, string? Text);

public record ApplyScenarioDto(int PatientId);

/// <summary>Предпросмотр: что запланируется, если подтвердить визит с этими услугами (по умолчанию — услуги визита).</summary>
public record PreviewDto(int VisitId, IReadOnlyList<string>? ServiceIds = null);

public record ReminderDraftDto(int ScenarioId, string ScenarioName, ScenarioPurpose Purpose, DateTime DueAt, string Text, string? Note);
