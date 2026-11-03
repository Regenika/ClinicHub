namespace ClinicHub.Core.Entities;

/// <summary>
/// Правило персональной авто-рассылки по событию (день рождения и т.п.).
/// Структура намеренно общая: новый Trigger добавляется без изменения схемы.
/// </summary>
public class AutoRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public AutoRuleTrigger Trigger { get; set; } = AutoRuleTrigger.Birthday;

    /// <summary>Поздравление со скидкой — реклама, поэтому по умолчанию Marketing.</summary>
    public MessagePurpose Purpose { get; set; } = MessagePurpose.Marketing;

    /// <summary>Шаблон текста. Плейсхолдер {name} заменяется на ФИО пациента.</summary>
    public string TextTemplate { get; set; } = "";

    /// <summary>Смещение от даты события в днях (0 — в сам день события).</summary>
    public int DaysOffset { get; set; }

    /// <summary>Не раньше какого времени суток (локальное время сервера) отправлять.</summary>
    public TimeSpan SendTime { get; set; } = new TimeSpan(10, 0, 0);

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }

    public List<AutoRuleFire> Fires { get; set; } = new();
}
