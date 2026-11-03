namespace ClinicHub.Core.Entities;

public class RequestRecipient
{
    public int Id { get; set; }
    public int RequestId { get; set; }
    public SmsRequest? Request { get; set; }

    public int? PatientId { get; set; }
    public Patient? Patient { get; set; }

    public string Phone { get; set; } = "";
    public string MessageText { get; set; } = "";

    /// <summary>Число SMS-сегментов (после расчёта по кодировке текста).</summary>
    public int SmsSegments { get; set; }

    /// <summary>Момент события (приём/запись). Служит базой для режимов "за день/час".</summary>
    public DateTime? AppointmentAt { get; set; }

    /// <summary>Раньше этого момента отправлять нельзя.</summary>
    public DateTime? SendAfter { get; set; }

    public RecipientStatus Status { get; set; } = RecipientStatus.Pending;

    /// <summary>Почему не отправлено (для Status = Skipped): «нет согласия на рекламу по SMS» и т. п.</summary>
    public string? SkipReason { get; set; }
    public DateTime? SentAt { get; set; }

    public List<SmsQueueItem> QueueItems { get; set; } = new();
}