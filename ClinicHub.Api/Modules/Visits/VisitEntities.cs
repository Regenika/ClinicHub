using ClinicHub.Core.Entities;

namespace ClinicHub.Api.Modules.Visits;

// Визиты пациентов — собственная копия в ClinicHub (ADR-0008 §1).
// Сейчас приходят из Dikidi (Planned) и подтверждаются администратором, либо вводятся руками.
// После МИС — приходят из неё уже со статусом и услугами.

public enum VisitStatus
{
    /// <summary>Запись есть, визита ещё не было.</summary>
    Planned,
    Completed,
    NoShow,
    Cancelled,
}

/// <summary>Откуда пришёл визит (и сопоставление внешних названий услуг).</summary>
public enum VisitSource
{
    Manual,
    Dikidi,
    Mis,
}

public class Visit
{
    public int Id { get; set; }

    public int PatientId { get; set; }
    public Patient? Patient { get; set; }

    /// <summary>Начало визита, UTC.</summary>
    public DateTime StartAt { get; set; }

    /// <summary>Врач из каталога, если известен. Может пропасть, если врача удалят из каталога.</summary>
    public string? DoctorId { get; set; }

    /// <summary>Снимок имени врача на момент визита: история не меняется при правке каталога.</summary>
    public string DoctorName { get; set; } = "";

    public VisitStatus Status { get; set; } = VisitStatus.Planned;
    public VisitSource Source { get; set; } = VisitSource.Manual;

    /// <summary>Идентификатор записи в Dikidi или МИС.</summary>
    public string? ExternalId { get; set; }

    public string? Comment { get; set; }

    /// <summary>Кто подтвердил итог визита (состоялся, неявка, отмена).</summary>
    public int? ConfirmedById { get; set; }
    public User? ConfirmedBy { get; set; }
    public DateTime? ConfirmedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<VisitService> Services { get; set; } = [];
}

/// <summary>Процедура визита. Название — снимок: переименование услуги в каталоге историю не меняет.</summary>
public class VisitService
{
    public int Id { get; set; }

    public int VisitId { get; set; }
    public Visit? Visit { get; set; }

    public string ServiceId { get; set; } = "";
    public string ServiceName { get; set; } = "";
}

/// <summary>
/// Сопоставление названия услуги во внешней системе с услугой каталога.
/// Непонятное название администратор сопоставляет один раз, дальше оно подставляется само.
/// </summary>
public class ExternalServiceMap
{
    public int Id { get; set; }
    public VisitSource Source { get; set; }

    /// <summary>Название или код услуги во внешней системе, как оно приходит.</summary>
    public string ExternalKey { get; set; } = "";

    public string ServiceId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
