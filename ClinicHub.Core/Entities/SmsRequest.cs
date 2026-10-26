namespace ClinicHub.Core.Entities;

public class SmsRequest
{
    public int Id { get; set; }
    public RequestType Type { get; set; } = RequestType.Personal;
    public string Title { get; set; } = "";
    public RequestStatus Status { get; set; } = RequestStatus.Created;
    public SendMode SendMode { get; set; } = SendMode.Now;

    /// <summary>Цель: от неё зависит проверка согласий. Не указано — считаем рекламой (перестраховка).</summary>
    public MessagePurpose Purpose { get; set; } = MessagePurpose.Marketing;

    /// <summary>Момент, когда заявка (как целое) должна превратиться в очередь.</summary>
    public DateTime? PlannedSendAt { get; set; }

    /// <summary>Шаблон текста. Для персональных записей может содержать плейсхолдеры.</summary>
    public string? TextTemplate { get; set; }

    /// <summary>Внешний идентификатор (для интеграции с сайтом клиники).</summary>
    public string? ExternalId { get; set; }

    public int? CreatedById { get; set; }
    public User? CreatedBy { get; set; }

    public int? ApprovedById { get; set; }
    public User? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<RequestRecipient> Recipients { get; set; } = new();
}