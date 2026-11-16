using ClinicHub.Api.Modules.Consents;
using ClinicHub.Api.Modules.Shared;
using ClinicHub.Api.Services;
using ClinicHub.Core.Dtos;
using ClinicHub.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Endpoints;

public static class RequestsEndpoints
{
    public static IEndpointRouteBuilder MapRequestsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/requests")
            .WithTags("Заявки")
            .RequireAuthorization();

        group.MapGet("/", GetList);
        group.MapGet("/{id:int}", GetById);
        group.MapPost("/", Create);
        group.MapPost("/{id:int}/approve", Approve).RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });
        group.MapPost("/{id:int}/reject", Reject).RequireAuthorization(new AuthorizeAttribute { Roles = "Admin" });
        group.MapPost("/{id:int}/send-now", SendNow);
        group.MapGet("/{id:int}/status", GetStatus);
        // Веб-панель (ADR-0012): пауза, продолжение и остановка рассылки, расчёт до запуска, журнал по получателям.
        group.MapPost("/{id:int}/pause", Pause);
        group.MapPost("/{id:int}/resume", Resume);
        group.MapPost("/{id:int}/cancel", Cancel);
        group.MapPost("/estimate", Estimate);
        group.MapGet("/recipients", GetRecipients);

        return app;
    }

    private static async Task<IResult> GetList(
        Data.ClinicHubDbContext db,
        string? status,
        string? type,
        int? take)
    {
        var query = db.SmsRequests.AsQueryable();

        if (Enum.TryParse<RequestStatus>(status, true, out var rs))
            query = query.Where(r => r.Status == rs);
        if (Enum.TryParse<RequestType>(type, true, out var rt))
            query = query.Where(r => r.Type == rt);

        var requests = await query
            .OrderByDescending(r => r.CreatedAt)
            .Take(take ?? 200)
            .Select(r => new SmsRequestOutDto
            {
                Id = r.Id,
                Type = r.Type.ToString(),
                Purpose = r.Purpose.ToString(),
                Title = r.Title,
                Status = r.Status.ToString(),
                SendMode = r.SendMode.ToString(),
                PlannedSendAt = r.PlannedSendAt,
                TextTemplate = r.TextTemplate,
                ExternalId = r.ExternalId,
                CreatedAt = r.CreatedAt,
                RecipientCount = r.Recipients.Count,
                SentCount = r.Recipients.Count(x => x.Status == RecipientStatus.Sent),
                FailedCount = r.Recipients.Count(x => x.Status == RecipientStatus.Failed),
                SkippedCount = r.Recipients.Count(x => x.Status == RecipientStatus.Skipped),
            })
            .ToListAsync();

        return Results.Ok(requests);
    }

    private static async Task<IResult> GetById(int id, Data.ClinicHubDbContext db)
    {
        var request = await db.SmsRequests
            .Where(r => r.Id == id)
            .Select(r => new SmsRequestOutDto
            {
                Id = r.Id,
                Type = r.Type.ToString(),
                Purpose = r.Purpose.ToString(),
                Title = r.Title,
                Status = r.Status.ToString(),
                SendMode = r.SendMode.ToString(),
                PlannedSendAt = r.PlannedSendAt,
                TextTemplate = r.TextTemplate,
                ExternalId = r.ExternalId,
                CreatedAt = r.CreatedAt,
                RecipientCount = r.Recipients.Count,
                SentCount = r.Recipients.Count(x => x.Status == RecipientStatus.Sent),
                FailedCount = r.Recipients.Count(x => x.Status == RecipientStatus.Failed),
                SkippedCount = r.Recipients.Count(x => x.Status == RecipientStatus.Skipped),
                Recipients = r.Recipients.Select(rec => new RecipientOutDto
                {
                    Id = rec.Id,
                    PatientId = rec.PatientId,
                    PatientName = rec.Patient != null ? rec.Patient.FullName : null,
                    Phone = rec.Phone,
                    MessageText = rec.MessageText,
                    SmsSegments = rec.SmsSegments,
                    AppointmentAt = rec.AppointmentAt,
                    SendAfter = rec.SendAfter,
                    Status = rec.Status.ToString(),
                    SentAt = rec.SentAt,
                    SkipReason = rec.SkipReason,
                }).ToList(),
            })
            .FirstOrDefaultAsync();

        return request is null ? Results.NotFound() : Results.Ok(request);
    }

    public static async Task<IResult> Create(
        CreateSmsRequestDto dto,
        Data.ClinicHubDbContext db,
        IHttpContextAccessor http,
        CancellationToken ct)
    {
        if (PurposeError(dto) is { } purposeError)
            return Results.BadRequest(purposeError);

        // Шага «одобрить» больше нет (ADR-0012): проверка — в мастере перед запуском, согласия — при отправке.
        var request = await CreateRequestCoreAsync(
            dto, db, http.HttpContext?.User.GetUserId(),
            approveAutomatically: true, ct);

        if (request == null)
            return Results.BadRequest("Не удалось собрать получателей или текст сообщения.");

        return Results.Created($"/api/requests/{request.Id}", ToOutDto(request));
    }

    /// <summary>
    /// Цель обязательна для новых заявок (Issue #34): от неё зависит, нужна ли проверка согласия на рекламу.
    /// </summary>
    public static string? PurposeError(CreateSmsRequestDto dto) =>
        dto.Purpose is { } p && Enum.IsDefined(p)
            ? null
            : "Укажите purpose: Service — напоминание о записи, перенос, контроль после визита; " +
              "Marketing — реклама (акции, поздравления со скидкой), уйдёт только пациентам с согласием на рекламу.";

    /// <summary>Общая логика создания заявки (используется и вебхуком). Возвращает null, если валидация не прошла.</summary>
    public static async Task<SmsRequest?> CreateRequestCoreAsync(
        CreateSmsRequestDto dto,
        Data.ClinicHubDbContext db,
        int? createdById,
        bool approveAutomatically,
        CancellationToken ct)
    {
        if (dto.Type == RequestType.Personal && dto.Recipients.Count != 1)
            return null;

        if (string.IsNullOrWhiteSpace(dto.TextTemplate) &&
            dto.Recipients.All(r => string.IsNullOrWhiteSpace(r.MessageText)))
        {
            return null;
        }

        var inputs = new List<RecipientInputDto>(dto.Recipients);
        if (dto.AllSubscribers)
        {
            var subscribers = await db.Patients
                .Where(p => p.IsSubscribed)
                .Select(p => new RecipientInputDto { PatientId = p.Id, Phone = p.Phone })
                .ToListAsync(ct);
            inputs.AddRange(subscribers.Where(s => !string.IsNullOrWhiteSpace(s.Phone)));
        }

        if (inputs.Count == 0)
            return null;

        var now = DateTime.UtcNow;
        var request = new SmsRequest
        {
            Type = dto.Type,
            // Без цели считаем рекламой: лучше не отправить, чем нарушить закон «О рекламе».
            Purpose = dto.Purpose ?? MessagePurpose.Marketing,
            Title = string.IsNullOrWhiteSpace(dto.Title)
                // Время в названии — московское (видит человек), а хранится всё в UTC.
                ? $"SMS-заявка {ClinicHub.Api.Modules.Shared.MoscowTime.ToMoscow(now):dd.MM.yyyy HH:mm}"
                : dto.Title,
            Status = approveAutomatically ? RequestStatus.Approved : RequestStatus.Created,
            SendMode = dto.SendMode,
            PlannedSendAt = dto.PlannedSendAt,
            TextTemplate = dto.TextTemplate,
            ExternalId = dto.ExternalId,
            CreatedById = createdById,
        };

        if (approveAutomatically)
        {
            request.ApprovedAt = now;
            request.ApprovedById = createdById;
        }

        foreach (var input in inputs)
        {
            string phone;
            var text = input.MessageText ?? dto.TextTemplate ?? "";
            // Если у конкретного получателя дата события не задана (например, при
            // AllSubscribers), берём общую дату события заявки — иначе режимы
            // "за день/час/2 недели" не смогут посчитать, когда слать.
            DateTime? appointmentAt = input.AppointmentAt ?? dto.AppointmentAt;

            if (input.PatientId is int pid)
            {
                var patient = await db.Patients.FirstOrDefaultAsync(p => p.Id == pid, ct);
                if (patient is null)
                    continue;
                phone = input.Phone ?? patient.Phone;
                if (string.IsNullOrWhiteSpace(phone))
                    continue;
            }
            else
            {
                phone = input.Phone ?? "";
                if (string.IsNullOrWhiteSpace(phone))
                    continue;
            }

            text = Personalize(text, input.PatientId is int id2
                ? (await db.Patients.Where(p => p.Id == id2).Select(p => p.FullName).FirstOrDefaultAsync(ct))
                : null);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            request.Recipients.Add(new RequestRecipient
            {
                PatientId = input.PatientId,
                Phone = phone,
                MessageText = text,
                SmsSegments = SmsSegmentCalculator.CountSegments(text),
                AppointmentAt = appointmentAt,
                SendAfter = dto.SendMode == SendMode.Now
                    ? now
                    : SchedulerService.ComputeSendAfter(dto.SendMode, appointmentAt, dto.PlannedSendAt),
            });
        }

        if (request.Recipients.Count == 0)
            return null;

        db.SmsRequests.Add(request);
        await db.SaveChangesAsync(ct);

        return request;
    }

    /// <summary>
    /// Подстановка имени: {name} или {Имя} — ФИО пациента, как оно записано в карточке. Получателю не из базы
    /// подставлять нечего: убираем метку и висящую запятую в начале.
    /// </summary>
    public static string Personalize(string text, string? fullName)
    {
        var name = fullName?.Trim() ?? "";
        var result = text
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{Имя}", name, StringComparison.OrdinalIgnoreCase);
        if (name.Length == 0)
            result = result.TrimStart(' ', ',', '!').TrimStart();
        return result.Trim();
    }

    /// <summary>Пауза: снимаем из очереди то, что ещё не выдано телефонам. Уже выданное телефону уйдёт.</summary>
    private static async Task<IResult> Pause(int id, Data.ClinicHubDbContext db, CancellationToken ct)
    {
        var request = await db.SmsRequests.Include(r => r.Recipients).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
            return Results.NotFound();
        if (request.Status is not (RequestStatus.Created or RequestStatus.Approved or RequestStatus.Scheduled or RequestStatus.Sending))
            return Results.BadRequest("Приостановить можно только рассылку, которая ещё идёт.");

        await DequeuePendingAsync(db, request, ct);
        request.Status = RequestStatus.Paused;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { request.Id, Status = request.Status.ToString() });
    }

    private static async Task<IResult> Resume(int id, Data.ClinicHubDbContext db, CancellationToken ct)
    {
        var request = await db.SmsRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
            return Results.NotFound();
        if (request.Status != RequestStatus.Paused)
            return Results.BadRequest("Рассылка не на паузе.");
        // Approved → планировщик заново поставит в очередь всех, кто ещё ждёт.
        request.Status = RequestStatus.Approved;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { request.Id, Status = request.Status.ToString() });
    }

    /// <summary>Остановить насовсем: неотправленным — «Пропущено: рассылку остановили».</summary>
    private static async Task<IResult> Cancel(int id, Data.ClinicHubDbContext db, CancellationToken ct)
    {
        var request = await db.SmsRequests.Include(r => r.Recipients).FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
            return Results.NotFound();
        if (request.Status is RequestStatus.Sent or RequestStatus.Failed or RequestStatus.Partial or RequestStatus.Rejected)
            return Results.BadRequest("Рассылка уже закончена.");

        await DequeuePendingAsync(db, request, ct);
        foreach (var rec in request.Recipients.Where(r => r.Status is RecipientStatus.Pending or RecipientStatus.Scheduled))
        {
            rec.Status = RecipientStatus.Skipped;
            rec.SkipReason = "рассылку остановили";
        }
        request.Status = request.Recipients.Any(r => r.Status == RecipientStatus.Sent) ? RequestStatus.Partial : RequestStatus.Rejected;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { request.Id, Status = request.Status.ToString() });
    }

    private static async Task DequeuePendingAsync(Data.ClinicHubDbContext db, SmsRequest request, CancellationToken ct)
    {
        var recipientIds = request.Recipients.Select(r => r.Id).ToList();
        var pending = await db.SmsQueue
            .Where(q => q.Status == QueueItemStatus.Pending && recipientIds.Contains(q.RecipientId))
            .ToListAsync(ct);
        db.SmsQueue.RemoveRange(pending);
        var back = pending.Select(q => q.RecipientId).ToHashSet();
        foreach (var rec in request.Recipients.Where(r => back.Contains(r.Id) && r.Status == RecipientStatus.Scheduled))
            rec.Status = RecipientStatus.Pending;
    }

    /// <summary>
    /// Расчёт до запуска (мастер «Новая рассылка», шаг «Проверка»): сколько получат, кого и почему пропустим,
    /// сколько SMS и когда закончим при текущих лимитах SIM и окне отправки. Ничего не сохраняет.
    /// </summary>
    private static async Task<IResult> Estimate(
        CreateSmsRequestDto dto, Data.ClinicHubDbContext db, IConsentChecker consents, CancellationToken ct)
    {
        if (PurposeError(dto) is { } purposeError)
            return Results.BadRequest(purposeError);
        var purpose = dto.Purpose!.Value;
        var now = DateTime.UtcNow;

        var inputs = new List<RecipientInputDto>(dto.Recipients);
        if (dto.AllSubscribers)
            inputs.AddRange(await db.Patients.Where(p => p.IsSubscribed)
                .Select(p => new RecipientInputDto { PatientId = p.Id }).ToListAsync(ct));

        var ids = inputs.Where(i => i.PatientId != null).Select(i => i.PatientId!.Value).Distinct().ToList();
        var patients = await db.Patients.Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.FullName, p.Phone }).ToDictionaryAsync(p => p.Id, ct);
        var decisions = ids.Count > 0
            ? await consents.CheckManyAsync(ids, purpose, ConsentChannel.Sms, now, ct)
            : new Dictionary<int, ConsentDecision>();

        var skipped = new Dictionary<string, int>();
        void Skip(string reason) => skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
        var recv = 0;
        var maxSegments = 0;
        var seen = new HashSet<int>();
        foreach (var input in inputs)
        {
            string? name = null;
            var phone = input.Phone;
            if (input.PatientId is int pid)
            {
                if (!seen.Add(pid))
                    continue;
                if (!patients.TryGetValue(pid, out var p)) { Skip("пациент удалён"); continue; }
                name = p.FullName;
                phone ??= p.Phone;
            }
            if (string.IsNullOrWhiteSpace(phone)) { Skip("нет телефона в карточке"); continue; }
            var decision = input.PatientId is int id ? decisions[id] : ConsentRules.ForUnknownRecipient(purpose);
            if (!decision.Allowed) { Skip(decision.Reason); continue; }
            recv++;
            var text = Personalize(input.MessageText ?? dto.TextTemplate ?? "", name);
            maxSegments = Math.Max(maxSegments, SmsSegmentCalculator.CountSegments(text));
        }

        // Скорость: сумма минутных лимитов включённых SIM на включённых телефонах; окно — из настроек отправки.
        var sims = await db.SimCards.Where(s => s.IsActive && s.Phone != null && s.Phone.IsActive)
            .Select(s => new { s.LimitPerMinute, s.LimitPerDay }).ToListAsync(ct);
        var rate = await db.RateConfigs.Where(r => r.IsActive).FirstOrDefaultAsync(ct) ?? new RateConfig();
        var perMinute = Math.Max(1, sims.Sum(s => Math.Max(1, s.LimitPerMinute)));
        var perDay = Math.Max(1, sims.Sum(s => Math.Max(1, s.LimitPerDay)));
        var start = dto.SendMode == SendMode.Custom && dto.PlannedSendAt is DateTime planned && planned > now ? planned : now;
        var finish = recv > 0 ? EstimateFinish(start, recv, perMinute, perDay, rate.SendWindowFrom, rate.SendWindowTo) : (DateTime?)null;

        return Results.Ok(new RequestEstimateDto
        {
            Total = seen.Count + inputs.Count(i => i.PatientId == null),
            Recipients = recv,
            Skipped = skipped.Select(kv => new SkipGroupDto { Reason = kv.Key, Count = kv.Value }).OrderByDescending(g => g.Count).ToList(),
            SegmentsPerMessage = maxSegments,
            TotalSms = recv * Math.Max(1, maxSegments),
            Phones = sims.Count,
            PerMinute = perMinute,
            StartsAt = start,
            FinishesAt = finish,
        });
    }

    /// <summary>Когда закончится отправка count сообщений со скоростью perMinute, только в окне [from; to] по Москве.</summary>
    public static DateTime EstimateFinish(DateTime startUtc, int count, int perMinute, int perDay, TimeSpan from, TimeSpan to)
    {
        var t = MoscowTime.ToMoscow(startUtc);
        var left = count;
        for (var guard = 0; guard < 400 && left > 0; guard++)
        {
            var dayStart = t.Date + from;
            var dayEnd = t.Date + to;
            if (t < dayStart) t = dayStart;
            if (t >= dayEnd) { t = t.Date.AddDays(1) + from; continue; }
            var canToday = Math.Min(perDay, (int)((dayEnd - t).TotalMinutes * perMinute));
            if (left <= canToday)
            {
                t = t.AddMinutes(Math.Ceiling((double)left / perMinute));
                left = 0;
                break;
            }
            left -= canToday;
            t = t.Date.AddDays(1) + from;
        }
        return MoscowTime.ToUtc(DateOnly.FromDateTime(t), TimeOnly.FromDateTime(t));
    }

    /// <summary>Журнал по получателям всех рассылок: что ушло, что нет и почему (раздел «Журнал» панели).</summary>
    private static async Task<IResult> GetRecipients(Data.ClinicHubDbContext db, string? status, int? requestId, int? patientId, int? take)
    {
        var query = db.RequestRecipients.AsQueryable();
        if (Enum.TryParse<RecipientStatus>(status, true, out var st))
            query = query.Where(r => r.Status == st);
        if (requestId is int rid)
            query = query.Where(r => r.RequestId == rid);
        if (patientId is int pid)
            query = query.Where(r => r.PatientId == pid);

        var rows = await query
            .OrderByDescending(r => r.SentAt ?? r.SendAfter ?? r.Request!.CreatedAt)
            .Take(Math.Clamp(take ?? 200, 1, 1000))
            .Select(r => new RecipientLogDto
            {
                Id = r.Id,
                RequestId = r.RequestId,
                RequestTitle = r.Request!.Title,
                RequestType = r.Request.Type.ToString(),
                Purpose = r.Request.Purpose.ToString(),
                PatientId = r.PatientId,
                PatientName = r.Patient != null ? r.Patient.FullName : null,
                Phone = r.Phone,
                MessageText = r.MessageText,
                Status = r.Status.ToString(),
                SkipReason = r.SkipReason,
                SendAfter = r.SendAfter,
                SentAt = r.SentAt,
                CreatedAt = r.Request.CreatedAt,
                LastError = r.QueueItems.OrderByDescending(q => q.Id).Select(q => q.LastError).FirstOrDefault(),
            })
            .ToListAsync();
        return Results.Ok(rows);
    }

    private static async Task<IResult> Approve(
        int id,
        Data.ClinicHubDbContext db,
        IHttpContextAccessor http,
        CancellationToken ct)
    {
        var request = await db.SmsRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
            return Results.NotFound();

        if (request.Status is not (RequestStatus.Created or RequestStatus.Rejected or RequestStatus.Approved))
            return Results.BadRequest($"Нельзя одобрить заявку в статусе {request.Status}.");

        request.Status = RequestStatus.Approved;
        request.ApprovedById = http.HttpContext?.User.GetUserId();
        request.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { request.Id, Status = request.Status.ToString() });
    }

    private static async Task<IResult> Reject(
        int id,
        Data.ClinicHubDbContext db,
        CancellationToken ct)
    {
        var request = await db.SmsRequests.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
            return Results.NotFound();

        if (request.Status is RequestStatus.Sent or RequestStatus.Sending or RequestStatus.Failed)
            return Results.BadRequest($"Нельзя отменить заявку в статусе {request.Status}.");

        request.Status = RequestStatus.Rejected;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { request.Id, Status = request.Status.ToString() });
    }

    /// <summary>"Отправить сейчас": немедленно переводит заявку в очередь и наружу.</summary>
    private static async Task<IResult> SendNow(
        int id,
        Data.ClinicHubDbContext db,
        CancellationToken ct)
    {
        var request = await db.SmsRequests
            .Include(r => r.Recipients)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
        if (request is null)
            return Results.NotFound();

        if (request.Status is RequestStatus.Sent or RequestStatus.Sending or RequestStatus.Partial or RequestStatus.Failed)
            return Results.BadRequest($"Нельзя отправить заявку в статусе {request.Status}.");

        request.SendMode = SendMode.Now;
        request.PlannedSendAt = DateTime.UtcNow;
        request.Status = RequestStatus.Approved;

        var now = DateTime.UtcNow;
        foreach (var rec in request.Recipients)
        {
            rec.SendAfter = now;
            if (rec.Status == RecipientStatus.Failed)
                rec.Status = RecipientStatus.Pending;
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { request.Id, Status = request.Status.ToString() });
    }

    private static async Task<IResult> GetStatus(int id, Data.ClinicHubDbContext db)
    {
        var data = await db.SmsRequests
            .Where(r => r.Id == id)
            .Select(r => new
            {
                r.Id,
                Status = r.Status.ToString(),
                Total = r.Recipients.Count,
                Sent = r.Recipients.Count(x => x.Status == RecipientStatus.Sent),
                Failed = r.Recipients.Count(x => x.Status == RecipientStatus.Failed),
                Skipped = r.Recipients.Count(x => x.Status == RecipientStatus.Skipped),
                InQueue = r.Recipients.Count(x => x.Status == RecipientStatus.Scheduled ||
                                                    x.Status == RecipientStatus.Sending),
            })
            .FirstOrDefaultAsync();

        return data is null ? Results.NotFound() : Results.Ok(data);
    }

    private static SmsRequestOutDto ToOutDto(SmsRequest r) => new()
    {
        Id = r.Id,
        Type = r.Type.ToString(),
        Purpose = r.Purpose.ToString(),
        Title = r.Title,
        Status = r.Status.ToString(),
        SendMode = r.SendMode.ToString(),
        PlannedSendAt = r.PlannedSendAt,
        TextTemplate = r.TextTemplate,
        ExternalId = r.ExternalId,
        CreatedAt = r.CreatedAt,
        RecipientCount = r.Recipients.Count,
        SentCount = r.Recipients.Count(x => x.Status == RecipientStatus.Sent),
        FailedCount = r.Recipients.Count(x => x.Status == RecipientStatus.Failed),
        SkippedCount = r.Recipients.Count(x => x.Status == RecipientStatus.Skipped),
    };
}