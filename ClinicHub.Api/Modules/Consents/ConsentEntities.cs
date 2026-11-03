using ClinicHub.Core.Entities;

namespace ClinicHub.Api.Modules.Consents;

// Согласия пациентов (PLAN 1.2, ARCHITECTURE §3.2, Issue #34). Юридически значимый журнал:
// только дописывается, текущее состояние вычисляется (ConsentRules). Изменение и удаление записей
// запрещены в ClinicHubDbContext.SaveChanges, а в API таких методов нет.

/// <summary>Канал связи. Any — согласие или отзыв сразу на все каналы.</summary>
public enum ConsentChannel
{
    Sms,
    Telegram,
    Vk,
    Max,
    Email,
    Any,
}

/// <summary>Откуда пришла запись журнала.</summary>
public enum ConsentSource
{
    /// <summary>Бумажная форма, внесена администратором (EvidenceRef — номер формы).</summary>
    PaperForm,
    /// <summary>Форма на сайте (EvidenceRef — номер заявки на сайте).</summary>
    Site,
    /// <summary>Бот в мессенджере.</summary>
    Bot,
    /// <summary>Устно администратору (лучше подкреплять бумажной формой).</summary>
    Verbal,
    /// <summary>Импорт таблицы. Согласия на рекламу не даёт (ARCHITECTURE §3.2), только отзыв.</summary>
    Import,
    /// <summary>Ответ пациента по SMS, например «СТОП» (PLAN 1.4).</summary>
    Sms,
}

public class ConsentRecord
{
    public long Id { get; set; }

    public int PatientId { get; set; }
    public Patient? Patient { get; set; }

    public MessagePurpose Purpose { get; set; }
    public ConsentChannel Channel { get; set; }

    /// <summary>true — согласие дано, false — отозвано (или отказ).</summary>
    public bool Granted { get; set; }

    public ConsentSource Source { get; set; }

    /// <summary>Чем подтверждается: номер бумажной формы, ID заявки с сайта. Сканы пока не храним.</summary>
    public string? EvidenceRef { get; set; }

    /// <summary>Когда дано или отозвано (UTC). Может быть в прошлом: бумажную форму вносят позже.</summary>
    public DateTime At { get; set; }

    /// <summary>Кто внёс запись (пользователь сервера). null — сайт, бот, SMS.</summary>
    public int? ById { get; set; }
    public User? By { get; set; }

    public string? Comment { get; set; }

    /// <summary>Когда запись попала в журнал (UTC), в отличие от At — когда согласие дали.</summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
