using ClinicHub.Core.Entities;

namespace ClinicHub.Core.Dtos;

public class RecipientInputDto
{
    public int? PatientId { get; set; }

    /// <summary>Номер получателя (если PatientId не задан или у пациента свой номер на этот случай).</summary>
    public string? Phone { get; set; }

    /// <summary>Персональный текст (если нужен не шаблонный).</summary>
    public string? MessageText { get; set; }

    /// <summary>Момент события/приёма (для режимов "за день/час").</summary>
    public DateTime? AppointmentAt { get; set; }
}

public class CreateSmsRequestDto
{
    public RequestType Type { get; set; }

    /// <summary>Обязательно: Service (напоминание о записи, перенос) или Marketing (реклама — только с согласием).</summary>
    public MessagePurpose? Purpose { get; set; }
    public string Title { get; set; } = "";
    public SendMode SendMode { get; set; } = SendMode.Now;

    /// <summary>Конкретный момент отправки для режима Custom.</summary>
    public DateTime? PlannedSendAt { get; set; }

    public string? TextTemplate { get; set; }
    public string? ExternalId { get; set; }

    /// <summary>
    /// Момент события/приёма на уровне всей заявки (например, общее время записи для
    /// массовой рассылки). Используется как AppointmentAt для получателей, у которых он
    /// не задан индивидуально — в том числе для режима AllSubscribers.
    /// </summary>
    public DateTime? AppointmentAt { get; set; }

    /// <summary>Получатели (для Mass — список). Для Personal — один.</summary>
    public List<RecipientInputDto> Recipients { get; set; } = new();

    /// <summary>Быстрая массовая рассылка: выбрать всех подписчиков (IsSubscribed).</summary>
    public bool AllSubscribers { get; set; }
}

public class RecipientOutDto
{
    public int Id { get; set; }
    public int? PatientId { get; set; }
    public string? PatientName { get; set; }
    public string Phone { get; set; } = "";
    public string MessageText { get; set; } = "";
    public int SmsSegments { get; set; }
    public DateTime? AppointmentAt { get; set; }
    public DateTime? SendAfter { get; set; }
    public string Status { get; set; } = "";
    public DateTime? SentAt { get; set; }

    /// <summary>Почему не отправлено (для статуса Skipped).</summary>
    public string? SkipReason { get; set; }
}

public class SmsRequestOutDto
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string SendMode { get; set; } = "";
    public DateTime? PlannedSendAt { get; set; }
    public string? TextTemplate { get; set; }
    public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; }
    public int RecipientCount { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }

    /// <summary>Пропущено по правилам (нет согласия, отписка).</summary>
    public int SkippedCount { get; set; }
    public List<RecipientOutDto> Recipients { get; set; } = new();
}