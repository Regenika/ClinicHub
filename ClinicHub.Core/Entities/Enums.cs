namespace ClinicHub.Core.Entities;

public enum UserRole
{
    Admin,
    Operator,
}

public enum RequestType
{
    Personal,
    Mass,
}

public enum RequestStatus
{
    Created,
    Approved,
    Scheduled,
    Sending,
    Sent,
    Partial,
    Failed,
    /// <summary>Остановлена человеком (или не одобрена в старой программе администратора).</summary>
    Rejected,
    /// <summary>На паузе: неотправленные ждут, пока рассылку не продолжат (веб-панель, ADR-0012).</summary>
    Paused,
}

/// <summary>
/// Цель сообщения (ARCHITECTURE §3.2, ADR-0008 §5). Service — продолжение лечения по визиту, идёт без рекламного
/// согласия. Marketing — реклама (акции, поздравления со скидкой, «давно не были»): только при согласии на канал
/// (закон «О рекламе», ст. 18).
/// </summary>
public enum MessagePurpose
{
    Service,
    Marketing,
}

public enum SendMode
{
    Now,
    Day,
    Hour,
    TwoWeeks,
    Custom,
}

public enum RecipientStatus
{
    Pending,
    Scheduled,
    Sending,
    Sent,
    Failed,
    /// <summary>Не отправлено по правилам: нет согласия на рекламу, пациент отписан и т. п. Причина — в SkipReason.</summary>
    Skipped,
}

public enum QueueItemStatus
{
    Pending,
    Assigned,
    Sent,
    Failed,
}

public enum SmsStatus
{
    Sent,
    Failed,
}

public enum AgentTaskResult
{
    Success,
    Failed,
}

/// <summary>Тип события для персональной авто-рассылки. Список закрытый, но легко расширяемый.</summary>
public enum AutoRuleTrigger
{
    /// <summary>День рождения пациента.</summary>
    Birthday,
}