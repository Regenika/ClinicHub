namespace ClinicHub.Core.Entities;

/// <summary>Откуда пациент попал в ClinicHub (ARCHITECTURE §4).</summary>
public enum PatientSource
{
    /// <summary>Заведён вручную (в том числе все пациенты, созданные до появления этого поля).</summary>
    Manual,
    /// <summary>Импорт Google-таблицы.</summary>
    Sheets,
    /// <summary>Создан адаптером Dikidi по записи с новым телефоном.</summary>
    Dikidi,
    /// <summary>Онлайн-запись на сайте.</summary>
    Site,
    Mis,
}

public class Patient
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string? Email { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public bool IsSubscribed { get; set; } = true;
    public string? Notes { get; set; }

    /// <summary>Начало последнего состоявшегося визита (UTC). Пересчитывается модулем Visits.</summary>
    public DateTime? LastVisitAt { get; set; }

    public PatientSource Source { get; set; } = PatientSource.Manual;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
