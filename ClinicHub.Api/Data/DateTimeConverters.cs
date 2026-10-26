using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ClinicHub.Api.Data;

/// <summary>
/// Момент времени в БД. Храним UTC (CLAUDE.md, ADR-0009).
/// В код приходят DateTime с разным Kind: Utc, Unspecified (из JSON без «Z») и Local. Без конвертера Local
/// записался бы в базу как есть, со сдвигом на 3 часа. При записи:
///   Utc — как есть; Local — переводим в UTC; Unspecified — считаем, что это уже UTC (так было и раньше).
/// При чтении помечаем значение как Utc, чтобы JSON отдавал время с «Z».
/// При переходе на PostgreSQL конвертер обязателен: Npgsql отказывается писать в timestamptz DateTime с Kind ≠ Utc.
/// </summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => ToUtc(v),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    private static DateTime ToUtc(DateTime v) => v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };
}

/// <summary>
/// Календарная дата без времени (колонка <c>date</c>): дата рождения, день срабатывания авто-правила.
/// Часовой пояс здесь не нужен: переводить в UTC нельзя, иначе полночь по Москве превратится во вчерашний день.
/// </summary>
public sealed class DateOnlyDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => DateTime.SpecifyKind(v.Date, DateTimeKind.Unspecified),
    v => DateTime.SpecifyKind(v, DateTimeKind.Unspecified));
