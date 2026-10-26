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

        var request = await CreateRequestCoreAsync(
            dto, db, http.HttpContext?.User.GetUserId(),
            approveAutomatically: false, ct);

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
                ? $"SMS-заявка {now:dd.MM.yyyy HH:mm}"
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