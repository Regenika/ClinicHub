using ClinicHub.Api.Data;
using ClinicHub.Core;
using ClinicHub.Core.Dtos;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Services;

/// <summary>
/// Загрузка клиентской базы из файла (Dikidi «Клиенты» → Excel; веб-панель, ADR-0012).
/// Пациент ищется по телефону (E.164). Существующих не переписываем — только дописываем пустые поля.
/// Согласий не ставит: импорт не даёт согласия на рекламу (ARCHITECTURE §3.2).
/// </summary>
public static class PatientImport
{
    public const int MaxRows = 5000;

    public static async Task<PatientImportResultDto> ApplyAsync(ClinicHubDbContext db, PatientImportRequestDto request, CancellationToken ct)
    {
        var result = new PatientImportResultDto { Total = request.Rows.Count };

        // Разбор и дубли внутри файла.
        var rows = new Dictionary<string, PatientImportRowDto>();
        foreach (var row in request.Rows)
        {
            if (!PhoneNumbers.TryNormalizeRuMobile(row.Phone, out var phone))
            {
                result.BadPhone++;
                continue;
            }
            if (!rows.TryAdd(phone, row))
                result.Duplicates++;
        }

        var phones = rows.Keys.ToList();
        var existing = new Dictionary<string, Patient>();
        foreach (var chunk in phones.Chunk(500))
        {
            foreach (var p in await db.Patients.Where(p => chunk.Contains(p.Phone)).ToListAsync(ct))
                existing.TryAdd(p.Phone, p);
        }

        foreach (var (phone, row) in rows)
        {
            var name = Clean(row.FullName, 200);
            var email = Clean(row.Email, 200);
            var dob = row.DateOfBirth is { } d && d.Year >= 1900 && d.Date <= DateTime.UtcNow.Date ? d.Date : (DateTime?)null;

            if (existing.TryGetValue(phone, out var patient))
            {
                var changed = false;
                if (patient.DateOfBirth is null && dob is not null) { patient.DateOfBirth = dob; changed = true; }
                if (string.IsNullOrWhiteSpace(patient.Email) && email is not null) { patient.Email = email; changed = true; }
                if (string.IsNullOrWhiteSpace(patient.FullName) && name is not null) { patient.FullName = name; changed = true; }
                if (changed) result.Updated++; else result.Unchanged++;
                continue;
            }

            db.Patients.Add(new Patient
            {
                FullName = name ?? "Без имени",
                Phone = phone,
                Email = email,
                DateOfBirth = dob,
                IsSubscribed = true,
                Source = PatientSource.Dikidi,
            });
            result.Created++;
        }

        if (!request.DryRun)
            await db.SaveChangesAsync(ct);
        else
            db.ChangeTracker.Clear();
        return result;
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var s = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..max];
    }
}
