using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClinicHub.Api.Services;
using ClinicHub.Core.Dtos;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agent")
            .WithTags("SmsAgent (телефоны)");

        group.MapPost("/connect", Connect).AllowAnonymous();
        group.MapPost("/heartbeat", Heartbeat).AllowAnonymous();
        group.MapPost("/log", UploadLog).AllowAnonymous();

        var secured = app.MapGroup("/api/agent")
            .AddEndpointFilter<PhoneAuthFilter>()
            .WithTags("SmsAgent (телефоны)");

        secured.MapGet("/{phoneNumber}/tasks", GetTasks);
        secured.MapPost("/{phoneNumber}/tasks/{taskId:long}/result", PostResult);

        return app;
    }

    /// <summary>Сколько SMS записывается на автоматически созданную SIM (администратор потом правит в «Телефоны»).</summary>
    private const int DefaultSimBalance = 500;

    /// <summary>
    /// Подключение телефона одним нажатием в приложении. Один и тот же физический телефон ВСЕГДА получает
    /// одну и ту же запись: приложение присылает постоянный ключ устройства (DeviceId), из него сервер
    /// строит стабильный идентификатор «модель-хеш». Старые записи этого же телефона (созданные прежними
    /// версиями со случайным суффиксом) подхватываются и объединяются, лишние убираются.
    /// Защита — только локальная сеть клиники (для альфа-версии): отключить лишний телефон можно на вкладке «Телефоны».
    /// </summary>
    private static async Task<IResult> Connect(
        AgentConnectRequest dto,
        HttpContext http,
        Data.ClinicHubDbContext db,
        EventLog events,
        CancellationToken ct)
    {
        var name = MakeName(dto.DeviceName);
        var deviceId = (dto.DeviceId ?? "").Trim();
        var wanted = (dto.PhoneNumber ?? "").Trim();
        var from = http.Connection.RemoteIpAddress;
        if (from is not null && from.IsIPv4MappedToIPv6)
            from = from.MapToIPv4();

        var hash = deviceId.Length > 0 ? DeviceHash(deviceId) : "";
        var suffix = hash.Length > 0 ? "-" + hash : "";
        var target = hash.Length > 0 ? name + suffix : "";

        Phone? phone = null;
        var how = "";

        // 1) По постоянному ключу устройства — основной и надёжный способ.
        if (suffix.Length > 0)
        {
            phone = await db.Phones.Include(p => p.SimCards)
                .FirstOrDefaultAsync(p => p.PhoneNumber.EndsWith(suffix), ct);
            if (phone is not null)
                how = "узнан по ключу устройства";
        }

        // 2) По прежнему идентификатору + ключу (старая версия приложения без DeviceId).
        if (phone is null && wanted.Length > 0)
        {
            var byId = await db.Phones.Include(p => p.SimCards).FirstOrDefaultAsync(p => p.PhoneNumber == wanted, ct);
            if (byId is not null && AgentAuth.IsAuthorized(byId, http))
            {
                phone = byId;
                how = "узнан по прежнему идентификатору";
            }
        }

        // 3) Записи этого же телефона, созданные старыми версиями (имя + 4 случайных символа).
        var legacy = new List<Phone>();
        if (deviceId.Length > 0)
        {
            var rx = new Regex("^" + Regex.Escape(name) + "-[0-9a-f]{4}$");
            var candidates = await db.Phones.Include(p => p.SimCards)
                .Where(p => p.PhoneNumber.StartsWith(name + "-"))
                .ToListAsync(ct);
            legacy = candidates.Where(p => rx.IsMatch(p.PhoneNumber)).ToList();

            if (phone is null && legacy.Count > 0)
            {
                phone = legacy.OrderByDescending(p => p.LastSeenAt ?? DateTime.MinValue).ThenBy(p => p.Id).First();
                how = "подхвачена прежняя запись этого же телефона";
            }
        }

        var isNew = phone is null;
        if (phone is null)
        {
            string id;
            if (target.Length > 0)
            {
                id = target;
            }
            else
            {
                do
                {
                    id = $"{name}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(2)).ToLowerInvariant()}";
                }
                while (await db.Phones.AnyAsync(p => p.PhoneNumber == id, ct));
            }

            phone = new Phone { PhoneNumber = id, IsActive = true };
            phone.SimCards.Add(new SimCard
            {
                Number = id,
                Balance = DefaultSimBalance,
                LimitPerMinute = 1,
                LimitPerHour = 60,
                LimitPerDay = 300,
                IsActive = true,
            });
            db.Phones.Add(phone);
            how = "создана новая запись";
        }

        // Лишние записи этого же телефона (дубли): удаляем, если с ними ничего не связано, иначе отключаем.
        var removed = new List<string>();
        foreach (var dup in legacy.Where(p => p.Id != phone.Id))
        {
            var simIds = dup.SimCards.Select(s => s.Id).ToList();
            var used = await db.SmsQueue.AnyAsync(q => q.PhoneId == dup.Id
                                                      || (q.SimCardId != null && simIds.Contains(q.SimCardId.Value)), ct)
                       || await db.SmsLogs.AnyAsync(l => l.PhoneId == dup.Id
                                                        || (l.SimCardId != null && simIds.Contains(l.SimCardId.Value)), ct);
            if (used)
            {
                dup.IsActive = false;
                removed.Add($"«{dup.PhoneNumber}» отключена (есть история отправок)");
            }
            else
            {
                db.Phones.Remove(dup);
                removed.Add($"«{dup.PhoneNumber}» удалена");
            }
        }

        // Приводим идентификатор к стабильному виду «модель-хеш» (если запись старая).
        if (!isNew && target.Length > 0 && !phone.PhoneNumber.EndsWith(suffix, StringComparison.Ordinal)
            && !await db.Phones.AnyAsync(p => p.PhoneNumber == target && p.Id != phone.Id, ct))
        {
            var old = phone.PhoneNumber;
            foreach (var sim in phone.SimCards.Where(s => s.Number == old))
                sim.Number = target;
            phone.PhoneNumber = target;
            how += $", идентификатор «{old}» → «{target}»";
        }

        if (string.IsNullOrEmpty(phone.AgentToken))
            phone.AgentToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        phone.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        if (isNew)
        {
            var sim = phone.SimCards[0];
            db.BalanceLogs.Add(new BalanceLog
            {
                SimCardId = sim.Id,
                Change = sim.Balance,
                Reason = "Автоматически при подключении телефона",
                BalanceAfter = sim.Balance,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        events.Write("Подключение",
            $"Телефон «{phone.PhoneNumber}» (#{phone.Id}) подключился с адреса {from}: {how}. " +
            $"Устройство «{dto.DeviceName}», ключ устройства {(deviceId.Length > 0 ? "есть" : "ОТСУТСТВУЕТ (старая версия приложения)")}." +
            (removed.Count > 0 ? " Дубли: " + string.Join("; ", removed) + "." : "") +
            (phone.IsActive ? "" : " ВНИМАНИЕ: телефон отключён администратором."));

        return Results.Ok(new AgentConnectResponse
        {
            Ok = true,
            PhoneId = phone.Id,
            PhoneNumber = phone.PhoneNumber,
            Token = phone.AgentToken!,
            Message = !phone.IsActive
                ? "Телефон подключён, но отключён администратором (вкладка «Телефоны»)."
                : isNew ? "Телефон добавлен и подключён." : "Телефон подключён (запись уже была — дубль не создан).",
        });
    }

    private static string DeviceHash(string deviceId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("clinichub:" + deviceId)))[..8].ToLowerInvariant();

    /// <summary>Журнал с телефона: строки попадают в общий журнал сервера (logs/clinichub.log).</summary>
    private static IResult UploadLog(AgentLogRequest dto, EventLog events)
    {
        var who = string.IsNullOrWhiteSpace(dto.PhoneNumber) ? "ещё не подключён" : dto.PhoneNumber.Trim();
        if (who.Length > 64)
            who = who[..64];

        var count = 0;
        foreach (var line in (dto.Lines ?? new List<string>()).Take(300))
        {
            var text = line ?? "";
            if (text.Length > 3000)
                text = text[..3000] + "…";
            events.Write("Телефон " + who, text);
            count++;
        }
        return Results.Ok(new { Accepted = count });
    }

    private static string MakeName(string? raw)
    {
        var s = new string((raw ?? "").Replace(' ', '-')
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (s.Length > 20)
            s = s[..20];
        return s.Length == 0 ? "Phone" : s;
    }

    private static async Task<IResult> Heartbeat(
        HeartbeatRequest dto,
        HttpContext http,
        Data.ClinicHubDbContext db,
        EventLog events,
        CancellationToken ct)
    {
        var phone = await db.Phones.FirstOrDefaultAsync(p => p.PhoneNumber == dto.PhoneNumber, ct);
        if (phone is null)
            return Results.NotFound();
        if (!AgentAuth.IsAuthorized(phone, http))
            return Results.Unauthorized();
        if (!phone.IsActive)
            return Results.Ok(new { Ok = false, PendingTasks = 0, Message = "Телефон отключён администратором" });

        var wasOffline = phone.LastSeenAt is null || phone.LastSeenAt.Value < DateTime.UtcNow.AddMinutes(-2);
        phone.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        if (wasOffline)
            events.Write("Телефон", $"«{phone.PhoneNumber}» (#{phone.Id}) снова на связи.");

        var pendingCount = await db.SmsQueue.CountAsync(
            q => q.PhoneId == phone.Id && q.Status == QueueItemStatus.Assigned, ct);

        return Results.Ok(new { Ok = true, PendingTasks = pendingCount });
    }

    private static async Task<IResult> GetTasks(
        HttpContext http,
        Data.ClinicHubDbContext db,
        EventLog events,
        CancellationToken ct)
    {
        var phoneId = http.GetPhoneId();

        var tasks = await db.SmsQueue
            .Where(q => q.Status == QueueItemStatus.Assigned && q.PhoneId == phoneId)
            .Include(q => q.Recipient)
            .Include(q => q.SimCard)
            .OrderBy(q => q.PlannedAt)
            .Select(q => new AgentTaskDto
            {
                TaskId = q.Id,
                Phone = q.Recipient!.Phone,
                Message = q.Recipient.MessageText,
                SubscriptionId = q.SimCard != null ? q.SimCard.SubscriptionId : null,
                Segments = q.Recipient.SmsSegments,
            })
            .ToListAsync(ct);

        if (tasks.Count > 0)
            events.Write("Задачи", $"Телефон #{phoneId} запросил задачи, отдано: {tasks.Count} " +
                                   $"({string.Join(", ", tasks.Select(t => $"#{t.TaskId}→{t.Phone}, SIM {t.SubscriptionId?.ToString() ?? "по умолч."}"))})");

        return Results.Ok(new { Tasks = tasks });
    }

    private static async Task<IResult> PostResult(
        long taskId,
        AgentTaskResultRequest dto,
        HttpContext http,
        Data.ClinicHubDbContext db,
        Services.SchedulerService scheduler,
        Services.WebhookNotifier webhooks,
        EventLog events,
        CancellationToken ct)
    {
        var phoneId = http.GetPhoneId();

        var item = await db.SmsQueue.FirstOrDefaultAsync(q => q.Id == taskId, ct);
        if (item is null)
        {
            events.Write("ОШИБКА/Результат", $"Телефон #{phoneId} прислал отчёт по несуществующей задаче #{taskId}.");
            return Results.NotFound();
        }
        if (item.PhoneId != phoneId)
        {
            events.Write("ОШИБКА/Результат",
                $"Телефон #{phoneId} прислал отчёт по задаче #{taskId}, но она назначена телефону #{item.PhoneId?.ToString() ?? "—"} " +
                $"(статус {item.Status}) — отчёт отклонён.");
            return Results.Forbid();
        }

        var success = dto.Result.Equals("success", StringComparison.OrdinalIgnoreCase);
        events.Write("Результат", $"Телефон #{phoneId} сообщил по задаче #{taskId}: {(success ? "ОТПРАВЛЕНО" : "ОШИБКА")}, " +
                                  $"сегментов {dto.Segments}" + (string.IsNullOrEmpty(dto.Error) ? "" : $", причина: {dto.Error}"));
        QueueItemStatus newStatus;
        try
        {
            newStatus = await scheduler.ProcessTaskResultAsync(
                taskId, success, dto.Segments, dto.Error, ct);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }

        // Пересчёт статуса заявки и уведомление сайта, если она завершилась.
        var recipient = await db.RequestRecipients.FirstOrDefaultAsync(r => r.QueueItems.Any(q => q.Id == taskId), ct);
        if (recipient is not null)
        {
            var requestId = recipient.RequestId;
            var status = await RecomputeRequestStatusAsync(db, requestId, ct);
            _ = webhooks.NotifyIfTerminalAsync(requestId, status, ct);
        }

        events.Write("Результат", $"Задача #{taskId}: новый статус {newStatus}.");

        return Results.Ok(new
        {
            Accepted = true,
            TaskStatus = newStatus.ToString(),
        });
    }

    private static async Task<string> RecomputeRequestStatusAsync(
        Data.ClinicHubDbContext db, int requestId, CancellationToken ct)
    {
        var request = await db.SmsRequests
            .Include(r => r.Recipients)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
            return "";

        var recs = request.Recipients;
        if (recs.Count == 0)
            return request.Status.ToString();

        var failed = recs.Count(r => r.Status is RecipientStatus.Failed or RecipientStatus.Skipped);
        var sent = recs.Count(r => r.Status == RecipientStatus.Sent);

        if (Services.RequestOutcome.Final(recs) is { } final)
        {
            request.Status = final;
        }
        else if (sent > 0 || failed > 0)
        {
            request.Status = RequestStatus.Sending;
        }
        else
        {
            request.Status = RequestStatus.Scheduled;
        }

        await db.SaveChangesAsync(ct);
        return request.Status.ToString();
    }
}