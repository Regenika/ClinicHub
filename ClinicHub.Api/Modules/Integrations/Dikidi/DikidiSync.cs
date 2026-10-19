using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Shared;
using ClinicHub.Api.Modules.Visits;
using ClinicHub.Core;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Integrations.Dikidi;

/// <summary>
/// Перенос журнала Dikidi в визиты ClinicHub (Issue #15, ADR-0008 §1). Правила:
/// <list type="bullet">
/// <item>новая запись → визит <c>Planned</c>, <c>Source = Dikidi</c>, <c>ExternalId</c> = id записи;</item>
/// <item>пока визит <c>Planned</c>, время, врач, пациент и услуги обновляются из Dikidi;</item>
/// <item>отменена или удалена в Dikidi → <c>Cancelled</c>;</item>
/// <item>итог визита (<c>Completed</c>, <c>NoShow</c>, ручная отмена) ставит администратор — такие визиты (<c>ConfirmedAt</c> задан) адаптер не трогает;</item>
/// <item>визит, отменённый самим адаптером, возвращается в <c>Planned</c>, если запись снова активна;</item>
/// <item>пациент ищется по телефону (E.164), новый создаётся с <c>Source = Dikidi</c>;</item>
/// <item>услуги — через <see cref="ExternalServiceMap"/>; несопоставленные попадают в список для админки.</item>
/// </list>
/// </summary>
public sealed class DikidiSync(
    ClinicHubDbContext db, IDikidiClient client, DikidiOptions options, DikidiSyncState state,
    TimeProvider clock, ILogger<DikidiSync> logger)
{
    public const string VanishedComment = "Запись пропала из журнала Dikidi (удалена).";
    public const string CancelledComment = "Отменена в Dikidi.";

    /// <summary>
    /// Забрать журнал и применить. Ошибку записывает в состояние и пробрасывает дальше.
    /// null — синхронизация уже идёт (фоновая и ручная одновременно не запускаются).
    /// </summary>
    public async Task<DikidiSyncResult?> RunAsync(CancellationToken ct)
    {
        if (!await state.Gate.WaitAsync(0, ct))
            return null;
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            state.Started(now);
            var today = MoscowTime.Date(now);
            var from = today.AddDays(-Math.Max(0, options.DaysBack));
            var to = today.AddDays(Math.Max(1, options.DaysAhead));
            try
            {
                var journal = await client.GetJournalAsync(from, to, ct);
                var result = await ApplyAsync(journal, from, to, ct);
                state.Succeeded(clock.GetUtcNow().UtcDateTime, result);
                logger.LogInformation(
                    "Dikidi: записей {Records}, новых визитов {Created}, изменено {Updated}, отменено {Cancelled}, " +
                    "новых пациентов {Patients}, без телефона {NoPhone}, несопоставленных услуг {Unmapped}.",
                    result.Records, result.Created, result.Updated, result.Cancelled, result.PatientsCreated,
                    result.SkippedNoPhone, result.Unmapped.Count);
                return result;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Тексты DikidiException без данных пациентов. Прочие (ошибка БД и т. п.) — только сообщение, без стека с данными.
                var message = ex is DikidiException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                state.Failed(clock.GetUtcNow().UtcDateTime, message);
                throw;
            }
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>Применить уже полученный журнал за период [from; to] (даты по Москве).</summary>
    public async Task<DikidiSyncResult> ApplyAsync(DikidiJournal journal, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        int created = 0, updated = 0, cancelled = 0, patientsCreated = 0, noPhone = 0, untouched = 0;

        var maps = (await db.ExternalServiceMaps.AsNoTracking()
                .Where(m => m.Source == VisitSource.Dikidi)
                .Select(m => new { m.ExternalKey, m.ServiceId })
                .ToListAsync(ct))
            .GroupBy(m => m.ExternalKey.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().ServiceId, StringComparer.OrdinalIgnoreCase);
        var serviceNames = await db.Services.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var doctors = (await db.Doctors.AsNoTracking().Select(d => new { d.Id, d.Name }).ToListAsync(ct))
            .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        // Телефоны у старых пациентов введены как попало («8 (999) …»), поэтому сравниваем в E.164 в памяти.
        // Пациентов в клинике тысячи, не миллионы — это дёшево.
        var patientsByPhone = new Dictionary<string, int>();
        foreach (var p in await db.Patients.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Phone }).ToListAsync(ct))
        {
            if (PhoneNumbers.TryNormalizeRuMobile(p.Phone, out var e164))
                patientsByPhone.TryAdd(e164, p.Id);
        }
        var newPatients = new Dictionary<string, Patient>();

        var ids = journal.Records.Select(r => r.Id).Distinct().ToList();
        var existing = (await db.Visits.Include(v => v.Services)
                .Where(v => v.Source == VisitSource.Dikidi && v.ExternalId != null && ids.Contains(v.ExternalId))
                .ToListAsync(ct))
            .ToDictionary(v => v.ExternalId!);

        var unmapped = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in journal.Records)
        {
            existing.TryGetValue(r.Id, out var visit);
            // Итог визита отметил администратор (ConfirmedAt) — это его решение, Dikidi его не перебивает.
            if (visit is not null && visit.ConfirmedAt is not null)
            {
                untouched++;
                continue;
            }

            if (r.State == DikidiRecordState.Cancelled)
            {
                if (visit is { Status: VisitStatus.Planned })
                {
                    Cancel(visit, CancelledComment, now);
                    cancelled++;
                }
                continue;
            }

            // Визит, который отменил сам адаптер (запись пропала из окна или была отменена), а запись снова активна:
            // например, её перенесли дальше чем на DaysAhead, а потом дата вошла в окно. Возвращаем в Planned.
            var revived = false;
            if (visit is { Status: VisitStatus.Cancelled })
            {
                visit.Status = VisitStatus.Planned;
                if (visit.Comment is VanishedComment or CancelledComment)
                    visit.Comment = null;
                revived = true;
            }

            if (!PhoneNumbers.TryNormalizeRuMobile(r.ClientPhone, out var phone))
            {
                // Без телефона напомнить нельзя, а сопоставить пациента не по чему. Уже созданный визит оставляем как есть.
                noPhone++;
                continue;
            }

            int? patientId = null;
            Patient? newPatient = null;
            if (patientsByPhone.TryGetValue(phone, out var pid))
                patientId = pid;
            else if (!newPatients.TryGetValue(phone, out newPatient))
            {
                newPatient = new Patient
                {
                    FullName = Limit(r.ClientName?.Trim() is { Length: > 0 } n ? n : "Без имени (из Dikidi)", 200),
                    Phone = phone,
                    Source = PatientSource.Dikidi,
                    CreatedAt = now,
                };
                db.Patients.Add(newPatient);
                newPatients[phone] = newPatient;
                patientsCreated++;
            }

            var services = new List<(string Id, string Name)>();
            foreach (var name in r.Services)
            {
                if (maps.TryGetValue(name.Trim(), out var serviceId) && serviceNames.TryGetValue(serviceId, out var catalogName))
                {
                    if (services.All(s => s.Id != serviceId))
                        services.Add((serviceId, catalogName));
                }
                else
                {
                    unmapped[name.Trim()] = unmapped.GetValueOrDefault(name.Trim()) + 1;
                }
            }

            var startAt = MoscowTime.ToUtc(r.Date, r.Start);
            var doctorName = Limit(r.MasterName, 200);
            var doctorId = doctors.TryGetValue(r.MasterName.Trim(), out var did) ? did : null;

            if (visit is null)
            {
                visit = new Visit
                {
                    StartAt = startAt,
                    DoctorId = doctorId,
                    DoctorName = doctorName,
                    Status = VisitStatus.Planned,
                    Source = VisitSource.Dikidi,
                    ExternalId = r.Id,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                SetPatient(visit, patientId, newPatient);
                visit.Services.AddRange(services.Select(s => new VisitService { ServiceId = s.Id, ServiceName = s.Name }));
                db.Visits.Add(visit);
                existing[r.Id] = visit;
                created++;
                continue;
            }

            var changed = revived;
            if (newPatient is not null || visit.PatientId != patientId)
            {
                SetPatient(visit, patientId, newPatient);
                changed = true;
            }
            if (visit.StartAt != startAt) { visit.StartAt = startAt; changed = true; }
            if (visit.DoctorName != doctorName) { visit.DoctorName = doctorName; changed = true; }
            if (visit.DoctorId != doctorId) { visit.DoctorId = doctorId; changed = true; }

            var have = visit.Services.Select(s => s.ServiceId).Order().ToList();
            var want = services.Select(s => s.Id).Order().ToList();
            if (!have.SequenceEqual(want))
            {
                visit.Services.RemoveAll(s => !want.Contains(s.ServiceId));
                foreach (var s in services.Where(s => have.All(h => h != s.Id)))
                    visit.Services.Add(new VisitService { ServiceId = s.Id, ServiceName = s.Name });
                changed = true;
            }

            if (changed)
            {
                visit.UpdatedAt = now;
                updated++;
            }
        }

        // Удалённая в Dikidi запись просто пропадает из журнала. Отменяем такие визиты, только если журнал пришёл
        // не пустым: пустой ответ скорее похож на сбой, чем на удаление всех записей разом.
        if (journal.Records.Count > 0)
        {
            var fromUtc = MoscowTime.ToUtc(from, TimeOnly.MinValue);
            var toUtc = MoscowTime.ToUtc(to.AddDays(1), TimeOnly.MinValue);
            var returned = ids.ToHashSet();
            var vanished = await db.Visits
                .Where(v => v.Source == VisitSource.Dikidi && v.Status == VisitStatus.Planned && v.ConfirmedAt == null
                            && v.StartAt >= fromUtc && v.StartAt < toUtc)
                .ToListAsync(ct);
            foreach (var v in vanished.Where(v => v.ExternalId is null || !returned.Contains(v.ExternalId)))
            {
                Cancel(v, VanishedComment, now);
                cancelled++;
            }
        }

        await db.SaveChangesAsync(ct);

        return new DikidiSyncResult(
            from, to, journal.Records.Count, created, updated, cancelled, patientsCreated, noPhone,
            journal.SkippedBlocks, untouched,
            unmapped.OrderByDescending(u => u.Value).ThenBy(u => u.Key)
                .Select(u => new UnmappedDikidiService(u.Key, u.Value)).ToList());
    }

    private static void SetPatient(Visit visit, int? patientId, Patient? newPatient)
    {
        if (newPatient is not null)
        {
            visit.Patient = newPatient;
        }
        else
        {
            visit.PatientId = patientId!.Value;
        }
    }

    private static void Cancel(Visit visit, string comment, DateTime now)
    {
        visit.Status = VisitStatus.Cancelled;
        if (string.IsNullOrWhiteSpace(visit.Comment))
            visit.Comment = comment;
        visit.UpdatedAt = now;
    }

    private static string Limit(string s, int max) => s.Length <= max ? s : s[..max];
}
