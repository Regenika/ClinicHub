using System.Security.Claims;
using ClinicHub.Api.Data;
using ClinicHub.Api.Endpoints;
using ClinicHub.Core;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Consents;

public record ConsentRecordDto(
    long Id, MessagePurpose Purpose, ConsentChannel Channel, bool Granted, ConsentSource Source,
    string? EvidenceRef, DateTime At, DateTime RecordedAt, int? ById, string? ByLogin, string? Comment);

/// <summary>Текущее состояние по цели и каналу: «granted», «revoked» или «none» (записей нет).</summary>
public record ConsentStateDto(
    MessagePurpose Purpose, ConsentChannel Channel, string State, DateTime? At, long? RecordId, bool CanSend, string Reason);

public record PatientConsentsDto(int PatientId, bool IsSubscribed, IReadOnlyList<ConsentStateDto> Current, IReadOnlyList<ConsentRecordDto> Journal);

/// <summary>Новая запись журнала. At не указан — сейчас; можно в прошлом (бумажную форму вносят позже).</summary>
public record ConsentInputDto(
    MessagePurpose? Purpose, ConsentChannel? Channel, bool? Granted, ConsentSource? Source,
    string? EvidenceRef = null, string? Comment = null, DateTime? At = null);

/// <summary>Согласие на рекламу из формы на сайте. Channels — на какие каналы человек согласился (или отказался).</summary>
public record SiteConsentDto(
    string? Phone, string? Name, IReadOnlyList<ConsentChannel>? Channels, bool Granted = true,
    string? EvidenceRef = null, DateTime? At = null);

/// <summary>
/// API согласий (Issue #34). Только чтение и дописывание: изменения и удаления записей нет и не будет.
/// Экраны — в веб-админке отдельной задачей (PLAN 1.12).
/// </summary>
public static class ConsentEndpoints
{
    public static IEndpointRouteBuilder MapConsentEndpoints(this IEndpointRouteBuilder app)
    {
        var patients = app.MapGroup("/api/patients/{id:int}/consents").WithTags("Согласия").RequireAuthorization();
        patients.MapGet("/", GetForPatient);
        patients.MapPost("/", Add);

        // Сайт клиники: по ключу X-Webhook-Api-Key, как и существующий вебхук заявок.
        app.MapPost("/api/consents/site", FromSite).WithTags("Согласия")
            .AllowAnonymous()
            .AddEndpointFilter<WebhookAuthFilter>();

        return app;
    }

    private static async Task<IResult> GetForPatient(int id, ClinicHubDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var patient = await db.Patients.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (patient is null)
            return Results.NotFound();

        var records = await db.ConsentRecords.AsNoTracking().Include(r => r.By)
            .Where(r => r.PatientId == id)
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Id)
            .ToListAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var current = new List<ConsentStateDto>();
        foreach (var purpose in new[] { MessagePurpose.Marketing, MessagePurpose.Service })
        {
            foreach (var channel in ConsentRules.Channels)
            {
                var effective = ConsentRules.Effective(records, purpose, channel, now);
                var decision = ConsentRules.Evaluate(patient, records, purpose, channel, now);
                current.Add(new ConsentStateDto(
                    purpose, channel,
                    effective is null ? "none" : effective.Granted ? "granted" : "revoked",
                    effective?.At, effective?.Id, decision.Allowed, decision.Reason));
            }
        }

        return Results.Ok(new PatientConsentsDto(patient.Id, patient.IsSubscribed, current, records.Select(ToDto).ToList()));
    }

    /// <summary>Администратор вносит бумажную форму, устное согласие или отзыв.</summary>
    private static async Task<IResult> Add(
        int id, ConsentInputDto dto, ClinicHubDbContext db, ClaimsPrincipal user, TimeProvider clock, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == id, ct))
            return Results.NotFound();

        var now = clock.GetUtcNow().UtcDateTime;
        if (Validate(dto.Purpose, dto.Channel, dto.Granted, dto.Source, dto.EvidenceRef, dto.Comment, dto.At, now) is { } error)
            return Results.BadRequest(error);

        var record = NewRecord(id, dto.Purpose!.Value, dto.Channel!.Value, dto.Granted!.Value, dto.Source!.Value,
            dto.EvidenceRef, dto.Comment, dto.At, now);
        record.ById = user.GetUserId();
        db.ConsentRecords.Add(record);
        await db.SaveChangesAsync(ct);

        await db.Entry(record).Reference(r => r.By).LoadAsync(ct);
        return Results.Created($"/api/patients/{id}/consents", ToDto(record));
    }

    /// <summary>
    /// Согласие на рекламу с сайта. Пациента ищем по телефону (E.164, старые номера нормализуем),
    /// нет такого — создаём с Source = Site. Сайт (ClinicHub.Web) подключит Свят.
    /// </summary>
    private static async Task<IResult> FromSite(SiteConsentDto dto, ClinicHubDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (!PhoneNumbers.TryNormalizeRuMobile(dto.Phone, out var phone))
            return Results.BadRequest("Телефон — российский мобильный номер, например +79991234567.");
        if (dto.Channels is not { Count: > 0 })
            return Results.BadRequest("Укажите channels — на какие каналы дано согласие (Sms, Telegram, Vk, Max, Email или Any).");
        if (dto.Channels.Any(c => !Enum.IsDefined(c)))
            return Results.BadRequest("Неизвестный канал в channels.");
        if (dto.Name?.Length > 200)
            return Results.BadRequest("Имя длиннее 200 символов.");

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var channel in dto.Channels.Distinct())
        {
            if (Validate(MessagePurpose.Marketing, channel, dto.Granted, ConsentSource.Site, dto.EvidenceRef, null, dto.At, now) is { } error)
                return Results.BadRequest(error);
        }

        var patientId = await FindByPhoneAsync(db, phone, ct);
        var created = false;
        Patient? patient = null;
        if (patientId is null)
        {
            patient = new Patient
            {
                FullName = string.IsNullOrWhiteSpace(dto.Name) ? "Без имени (с сайта)" : dto.Name.Trim(),
                Phone = phone,
                Source = PatientSource.Site,
                CreatedAt = now,
            };
            db.Patients.Add(patient);
            created = true;
        }

        var records = dto.Channels.Distinct()
            .Select(channel => NewRecord(patientId ?? 0, MessagePurpose.Marketing, channel, dto.Granted, ConsentSource.Site,
                dto.EvidenceRef, null, dto.At, now))
            .ToList();
        foreach (var r in records)
        {
            if (patient is not null)
                r.Patient = patient;
            db.ConsentRecords.Add(r);
        }
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            PatientId = patient?.Id ?? patientId!.Value,
            PatientCreated = created,
            Records = records.Select(r => r.Id).ToList(),
        });
    }

    // ── Общее ───────────────────────────────────────────────────────────────

    private static string? Validate(
        MessagePurpose? purpose, ConsentChannel? channel, bool? granted, ConsentSource? source,
        string? evidence, string? comment, DateTime? at, DateTime now)
    {
        if (purpose is not { } p || !Enum.IsDefined(p))
            return "Укажите purpose: Service или Marketing.";
        if (channel is not { } c || !Enum.IsDefined(c))
            return "Укажите channel: Sms, Telegram, Vk, Max, Email или Any.";
        if (granted is null)
            return "Укажите granted: true — согласие дано, false — отозвано.";
        if (source is not { } s || !Enum.IsDefined(s))
            return "Укажите source: PaperForm, Site, Bot, Verbal, Import или Sms.";
        // ARCHITECTURE §3.2: архив и импорт согласия на рекламу не дают — только новая форма от пациента.
        if (s == ConsentSource.Import && p == MessagePurpose.Marketing && granted == true)
            return "Импорт не даёт согласия на рекламу: нужна новая форма согласия от пациента.";
        if (evidence?.Length > 100)
            return "evidenceRef длиннее 100 символов.";
        if (comment?.Length > 500)
            return "comment длиннее 500 символов.";
        if (at is DateTime a && ToUtc(a) > now.AddMinutes(5))
            return "Время согласия (at) не может быть в будущем.";
        return null;
    }

    private static ConsentRecord NewRecord(
        int patientId, MessagePurpose purpose, ConsentChannel channel, bool granted, ConsentSource source,
        string? evidence, string? comment, DateTime? at, DateTime now) => new()
    {
        PatientId = patientId,
        Purpose = purpose,
        Channel = channel,
        Granted = granted,
        Source = source,
        EvidenceRef = string.IsNullOrWhiteSpace(evidence) ? null : evidence.Trim(),
        Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim(),
        At = at is DateTime a ? ToUtc(a) : now,
        RecordedAt = now,
    };

    /// <summary>Время без пояса из JSON считаем UTC (как и везде в API), с поясом — переводим.</summary>
    private static DateTime ToUtc(DateTime t) => t.Kind switch
    {
        DateTimeKind.Utc => t,
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => DateTime.SpecifyKind(t, DateTimeKind.Utc),
    };

    /// <summary>Пациент по телефону. Старые номера введены как попало, поэтому сравниваем в E.164 в памяти.</summary>
    private static async Task<int?> FindByPhoneAsync(ClinicHubDbContext db, string e164, CancellationToken ct)
    {
        var exact = await db.Patients.AsNoTracking().Where(p => p.Phone == e164).OrderBy(p => p.Id).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
        if (exact is not null)
            return exact;
        foreach (var p in await db.Patients.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.Phone }).ToListAsync(ct))
        {
            if (PhoneNumbers.TryNormalizeRuMobile(p.Phone, out var n) && n == e164)
                return p.Id;
        }
        return null;
    }

    private static ConsentRecordDto ToDto(ConsentRecord r) => new(
        r.Id, r.Purpose, r.Channel, r.Granted, r.Source, r.EvidenceRef, r.At, r.RecordedAt, r.ById, r.By?.Login, r.Comment);
}
