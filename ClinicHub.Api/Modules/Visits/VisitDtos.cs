namespace ClinicHub.Api.Modules.Visits;

public record VisitServiceDto(string ServiceId, string ServiceName);

public record VisitDto(
    int Id, int PatientId, string? PatientName, DateTime StartAt, string? DoctorId, string DoctorName,
    VisitStatus Status, VisitSource Source, string? ExternalId, string? Comment,
    int? ConfirmedById, DateTime? ConfirmedAt, IReadOnlyList<VisitServiceDto> Services);

/// <summary>
/// Ручное создание визита (запись). Время — UTC или с указанием пояса («2026-10-20T10:00:00+03:00»).
/// Врач: DoctorId из каталога или просто имя, если врача в каталоге нет.
/// </summary>
public record CreateVisitDto(
    int PatientId, DateTime StartAt, string? DoctorId, string? DoctorName, IReadOnlyList<string>? ServiceIds, string? Comment);

/// <summary>
/// «Визит состоялся». ServiceIds — фактически выполненные процедуры: если передан, заменяет список услуг визита
/// (подставленный из записи можно поправить); если null — остаются услуги из записи.
/// </summary>
public record CompleteVisitDto(IReadOnlyList<string>? ServiceIds, string? DoctorId, string? Comment);

/// <summary>Неявка или отмена, с необязательным комментарием.</summary>
public record VisitNoteDto(string? Comment);

public record ServiceMapDto(int Id, VisitSource Source, string ExternalKey, string ServiceId);

public record ServiceMapInputDto(VisitSource Source, string ExternalKey, string ServiceId);
