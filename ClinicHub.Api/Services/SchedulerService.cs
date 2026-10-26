using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Consents;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Services;

/// <summary>
/// Серверное планирование и распределение очереди (REQ-SCHED, REQ-RATE).
/// Никакие решения здесь не принимаются на телефоне: сервер сам решает,
/// когда, кому и с какой SIM уйдёт каждое SMS.
/// </summary>
public class SchedulerService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SchedulerService> _logger;

    public SchedulerService(IServiceScopeFactory scopeFactory, ILogger<SchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public static DateTime? ComputeSendAfter(SendMode mode, DateTime? appointmentAt, DateTime? customPlanned)
    {
        return mode switch
        {
            SendMode.Now => DateTime.UtcNow,
            SendMode.Hour => appointmentAt?.AddHours(-1),
            SendMode.Day => appointmentAt?.AddDays(-1),
            SendMode.TwoWeeks => appointmentAt?.AddDays(-14),
            SendMode.Custom => customPlanned ?? DateTime.UtcNow,
            _ => appointmentAt,
        };
    }

    /// <summary>
    /// Превращает одобренные заявки в получателей и ставит их в очередь, когда разрешено.
    /// Перед постановкой — проверка согласий (Issue #34): кому нельзя, тот получает Skipped с причиной и в очередь не идёт.
    /// </summary>
    public async Task PlanAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHubDbContext>();
        var consents = scope.ServiceProvider.GetRequiredService<IConsentChecker>();

        var now = DateTime.UtcNow;

        // 1) Заявки Approved -> ставим статус Scheduled (безопасно при рестарте).
        var approved = await db.SmsRequests
            .Where(r => r.Status == RequestStatus.Approved)
            .Include(r => r.Recipients)
            .ToListAsync(ct);

        foreach (var request in approved)
        {
            // Прогреваем SendAfter у получателей, если ещё не рассчитан.
            foreach (var rec in request.Recipients)
            {
                if (rec.SendAfter == null)
                {
                    rec.SendAfter = ComputeSendAfter(request.SendMode, rec.AppointmentAt, request.PlannedSendAt);
                }
            }

            request.Status = RequestStatus.Scheduled;
            await EnqueueDueAsync(db, consents, request, now, ct);
            _logger.LogInformation("Заявка #{Id} запланирована ({Title})", request.Id, request.Title);
        }

        // 2) Заявки в статусе Scheduled: новые разрешённые получатели -> в очередь.
        var scheduled = await db.SmsRequests
            .Where(r => r.Status == RequestStatus.Scheduled)
            .Include(r => r.Recipients)
            .ToListAsync(ct);

        foreach (var request in scheduled.Concat(approved).Distinct())
        {
            if (request.Status != RequestStatus.Scheduled)
                continue;
            if (!approved.Contains(request))
                await EnqueueDueAsync(db, consents, request, now, ct);

            // Все получатели уже в конечном статусе (например, всех пропустили без согласия) — заявка завершена:
            // отчёт телефона, который обычно пересчитывает статус, в этом случае не придёт.
            if (RequestOutcome.Final(request.Recipients) is { } final)
                request.Status = final;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Наступившие получатели заявки: проверка согласий и постановка в очередь.</summary>
    private async Task EnqueueDueAsync(
        ClinicHubDbContext db, IConsentChecker consents, SmsRequest request, DateTime now, CancellationToken ct)
    {
        var due = request.Recipients
            .Where(rec => rec.Status == RecipientStatus.Pending && rec.SendAfter != null && rec.SendAfter.Value <= now)
            .ToList();
        if (due.Count == 0)
            return;

        var decisions = await consents.CheckManyAsync(
            due.Where(r => r.PatientId != null).Select(r => r.PatientId!.Value).Distinct().ToList(),
            request.Purpose, ConsentChannel.Sms, now, ct);

        foreach (var rec in due)
        {
            var decision = rec.PatientId is int pid ? decisions[pid] : ConsentRules.ForUnknownRecipient(request.Purpose);
            if (!decision.Allowed)
            {
                Skip(rec, decision.Reason);
                _logger.LogInformation("Заявка #{Req}: получатель #{Rec} пропущен — {Reason}", request.Id, rec.Id, decision.Reason);
                continue;
            }

            Enqueue(db, rec.Id);
            rec.Status = RecipientStatus.Scheduled;
            _logger.LogInformation("Заявка #{Req}: получатель #{Rec} ({To}) поставлен в очередь", request.Id, rec.Id, rec.Phone);
        }
    }

    private static void Skip(RequestRecipient rec, string reason)
    {
        rec.Status = RecipientStatus.Skipped;
        rec.SkipReason = reason.Length <= 300 ? reason : reason[..300];
    }

    /// <summary>Сколько ждать отчёта от устройства, прежде чем считать задачу зависшей.</summary>
    private static readonly TimeSpan StaleAssignedTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Возвращает в очередь задачи, которые устройство забрало (Assigned), но так и не
    /// отчиталось за StaleAssignedTimeout — телефон мог потерять связь, перезапуститься
    /// или приложение убили из фона. Без этого сообщение "зависает" навсегда: получатель
    /// не получает SMS, а лимиты SIM продолжают считать задачу забронированной.
    /// </summary>
    public async Task<int> RequeueStaleAsync(CancellationToken ct)
    {
        await using var db = _scopeFactory.CreateScope().ServiceProvider
            .GetRequiredService<ClinicHubDbContext>();

        var threshold = DateTime.UtcNow - StaleAssignedTimeout;

        var stale = await db.SmsQueue
            .Include(q => q.Recipient)
            .Where(q => q.Status == QueueItemStatus.Assigned
                     && q.PlannedAt != null && q.PlannedAt.Value < threshold)
            .ToListAsync(ct);

        if (stale.Count == 0)
            return 0;

        var maxAttemptsStr = await db.AppSettings
            .Where(s => s.Key == "MaxAttempts")
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct) ?? "3";
        var maxAttempts = int.TryParse(maxAttemptsStr, out var m) ? m : 3;

        foreach (var item in stale)
        {
            item.Attempts++;

            if (item.Attempts >= maxAttempts)
            {
                item.Status = QueueItemStatus.Failed;
                item.LastError = "Устройство не подтвердило отправку вовремя — превышено число попыток";
                if (item.Recipient != null)
                    item.Recipient.Status = RecipientStatus.Failed;
            }
            else
            {
                // Освобождаем SIM/телефон, чтобы задачу мог забрать любой доступный воркер заново.
                item.Status = QueueItemStatus.Pending;
                item.SimCardId = null;
                item.PhoneId = null;
                item.LastError = "Не получен отчёт от устройства вовремя — переназначено";
            }
        }

        await db.SaveChangesAsync(ct);
        _logger.LogWarning("Переназначено/провалено зависших задач: {N}", stale.Count);
        return stale.Count;
    }

    private static void Enqueue(ClinicHubDbContext db, int recipientId)
    {
        // Идемпотентность: не создаём второй элемент очереди для уже поставленного получателя.
        var exists = db.SmsQueue.Any(q =>
            q.RecipientId == recipientId && q.Status != QueueItemStatus.Failed);
        if (!exists)
        {
            db.SmsQueue.Add(new SmsQueueItem
            {
                RecipientId = recipientId,
                Status = QueueItemStatus.Pending,
                CreatedAt = DateTime.UtcNow,
            });
        }
    }

    private static readonly Dictionary<string, (string Text, DateTime At)> _diag = new();

    /// <summary>Пишет причину «почему не раздаётся» в журнал — но не чаще раза в 2 минуты для одного и того же текста.</summary>
    private void Diag(string key, string text)
    {
        lock (_diag)
        {
            if (_diag.TryGetValue(key, out var prev) && prev.Text == text && (DateTime.UtcNow - prev.At).TotalSeconds < 120)
                return;
            _diag[key] = (text, DateTime.UtcNow);
        }
        _logger.LogInformation("Планировщик: {Text}", text);
    }

    /// <summary>Распределяет Pending-элементы очереди по онлайн-телефонам/SIM с учётом лимитов.</summary>
    public async Task<int> DistributeAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHubDbContext>();

        var now = DateTime.UtcNow;

        var pendingTotal = await db.SmsQueue.CountAsync(q => q.Status == QueueItemStatus.Pending, ct);
        if (pendingTotal == 0)
            return 0;

        var rate = await db.RateConfigs.FirstOrDefaultAsync(c => c.IsActive, ct);
        if (rate == null)
        {
            Diag("rate", $"В очереди {pendingTotal} SMS, но нет активной конфигурации лимитов — раздавать нечего.");
            return 0;
        }

        // Окно отправки (локальное время сервера).
        var localNow = DateTime.Now.TimeOfDay;
        var inWindow = localNow >= rate.SendWindowFrom && localNow <= rate.SendWindowTo;
        if (!inWindow)
        {
            Diag("window", $"В очереди {pendingTotal} SMS, но сейчас {DateTime.Now:HH:mm} — вне окна отправки " +
                           $"{rate.SendWindowFrom:hh\\:mm}–{rate.SendWindowTo:hh\\:mm}. Они уйдут, когда окно откроется.");
            return 0;
        }

        // Онлайн-телефоны (LastSeenAt не старше 2 минут).
        var onlinePhones = await db.Phones
            .Where(p => p.IsActive && p.LastSeenAt != null && p.LastSeenAt.Value >= now.AddMinutes(-2))
            .Include(p => p.SimCards)
            .ToListAsync(ct);

        var sims = onlinePhones
            .SelectMany(p => p.SimCards)
            .Where(s => s.IsActive && s.Balance > 0)
            .ToList();

        if (sims.Count == 0)
        {
            var totalPhones = await db.Phones.CountAsync(ct);
            Diag("nosims", $"В очереди {pendingTotal} SMS, но нет ни одной подходящей SIM: телефонов на связи (за 2 мин) — " +
                           $"{onlinePhones.Count} из {totalPhones}; активных SIM с остатком > 0 среди них — 0.");
            return 0;
        }

        var simIds = sims.Select(s => s.Id).ToList();

        // Счётчики по каждой SIM на основе фактических отправок (источник истины — SmsLog).
        var sentSinceMinute = await db.SmsLogs
            .Where(l => l.Status == SmsStatus.Sent && l.SimCardId != null && simIds.Contains(l.SimCardId.Value)
                && l.SentAt >= now.AddMinutes(-1))
            .GroupBy(l => l.SimCardId!.Value)
            .Select(g => new { Sim = g.Key, Cnt = g.Count() })
            .ToDictionaryAsync(x => x.Sim, x => x.Cnt, ct);

        var sentSinceHour = await db.SmsLogs
            .Where(l => l.Status == SmsStatus.Sent && l.SimCardId != null && simIds.Contains(l.SimCardId.Value)
                && l.SentAt >= now.AddHours(-1))
            .GroupBy(l => l.SimCardId!.Value)
            .Select(g => new { Sim = g.Key, Cnt = g.Count() })
            .ToDictionaryAsync(x => x.Sim, x => x.Cnt, ct);

        var sentSinceDay = await db.SmsLogs
            .Where(l => l.Status == SmsStatus.Sent && l.SimCardId != null && simIds.Contains(l.SimCardId.Value)
                && l.SentAt >= now.Date)
            .GroupBy(l => l.SimCardId!.Value)
            .Select(g => new { Sim = g.Key, Cnt = g.Count() })
            .ToDictionaryAsync(x => x.Sim, x => x.Cnt, ct);

        // Последняя отправка по SIM (для минимального интервала).
        var lastSent = await db.SmsLogs
            .Where(l => l.Status == SmsStatus.Sent && l.SimCardId != null && simIds.Contains(l.SimCardId.Value))
            .GroupBy(l => l.SimCardId!.Value)
            .Select(g => new { Sim = g.Key, Max = g.Max(l => l.SentAt) })
            .ToDictionaryAsync(x => x.Sim, x => x.Max, ct);

        // Задачи, уже выданные телефонам (в полёте), — тоже стоят в "минутной" квоте.
        var inFlight = await db.SmsQueue
            .Where(q => q.Status == QueueItemStatus.Assigned && q.SimCardId != null && simIds.Contains(q.SimCardId.Value))
            .GroupBy(q => q.SimCardId!.Value)
            .Select(g => new { Sim = g.Key, Cnt = g.Count() })
            .ToDictionaryAsync(x => x.Sim, x => x.Cnt, ct);

        var pending = await db.SmsQueue
            .Where(q => q.Status == QueueItemStatus.Pending)
            .Include(q => q.Recipient)
            .ThenInclude(r => r!.Request)
            .OrderByDescending(q => q.Priority)
            .ThenBy(q => q.CreatedAt)
            .Take(500)
            .ToListAsync(ct);

        // Повторная проверка согласий прямо перед выдачей телефону: пока SMS ждала окна или свободной SIM,
        // пациент мог отозвать согласие или отписаться (Issue #34).
        var skipped = await RecheckConsentsAsync(db, scope.ServiceProvider.GetRequiredService<IConsentChecker>(), pending, now, ct);
        pending.RemoveAll(q => q.Status != QueueItemStatus.Pending);

        var assigned = 0;

        foreach (var item in pending)
        {
            if (item.Recipient == null || item.Recipient.SendAfter != null && item.Recipient.SendAfter.Value > now)
                continue;

            var segments = Math.Max(item.Recipient.SmsSegments, 1);

            // Выбор SIM: наименьшая загрузка текущего часа при доступных лимитах.
            SimCard? best = null;
            double bestLoad = double.MaxValue;

            var textHash = item.Recipient.MessageText.GetHashCode().ToString();
            var whys = new List<string>();

            foreach (var sim in sims)
            {
                if (sim.Balance < segments)
                {
                    whys.Add($"SIM #{sim.Id}: остаток {sim.Balance} < нужно {segments}");
                    continue;
                }

                // Авто-пауза SIM после серии ошибок подряд (см. ProcessTaskResultAsync).
                if (sim.PausedUntil is DateTime pausedUntil && pausedUntil > now)
                {
                    whys.Add($"SIM #{sim.Id}: на паузе до {pausedUntil.ToLocalTime():HH:mm:ss} (серия ошибок отправки)");
                    continue;
                }

                var minCnt = sentSinceMinute.GetValueOrDefault(sim.Id) + inFlight.GetValueOrDefault(sim.Id);
                if (minCnt >= sim.LimitPerMinute)
                {
                    whys.Add($"SIM #{sim.Id}: лимит в минуту {sim.LimitPerMinute} (уже {minCnt}, включая выданные телефону и ещё не подтверждённые)");
                    continue;
                }
                if (sentSinceHour.GetValueOrDefault(sim.Id) >= sim.LimitPerHour)
                {
                    whys.Add($"SIM #{sim.Id}: лимит в час {sim.LimitPerHour}");
                    continue;
                }
                if (sentSinceDay.GetValueOrDefault(sim.Id) >= sim.LimitPerDay)
                {
                    whys.Add($"SIM #{sim.Id}: лимит в сутки {sim.LimitPerDay}");
                    continue;
                }

                // Минимальный интервал между SMS с одной SIM + случайный джиттер: отправка
                // строго по таймеру — сама по себе спам-паттерн, который легко детектится оператором.
                var jitterSeconds = Random.Shared.Next(0, (int)Math.Ceiling(rate.MinIntervalSeconds * 0.6) + 1);
                if (lastSent.TryGetValue(sim.Id, out var last) &&
                    (now - last).TotalSeconds < rate.MinIntervalSeconds + jitterSeconds)
                {
                    whys.Add($"SIM #{sim.Id}: минимальный интервал между SMS ещё не прошёл");
                    continue;
                }

                var hourCapacity = sim.LimitPerHour == 0 ? 1.0 : (double)sim.LimitPerHour;
                var load = (sentSinceHour.GetValueOrDefault(sim.Id) + (double)inFlight.GetValueOrDefault(sim.Id)) / hourCapacity;

                // Штраф (не запрет) за повтор того же шаблона подряд с одной SIM — разносим
                // одинаковый текст по разным SIM/времени, как рекомендует антиспам-политика.
                if (sim.LastMessageHash == textHash &&
                    sim.LastMessageAt is DateTime lastMsgAt && (now - lastMsgAt).TotalMinutes < 10)
                {
                    load += 1.0;
                }

                if (load < bestLoad)
                {
                    bestLoad = load;
                    best = sim;
                }
            }

            if (best == null)
            {
                Diag($"item{item.Id}", $"Задача #{item.Id} (кому {item.Recipient.Phone}) пока ждёт: " + string.Join("; ", whys));
                continue;
            }

            _logger.LogInformation("Задача #{Id} (кому {To}) назначена: телефон #{PhoneId}, SIM #{SimId}",
                item.Id, item.Recipient.Phone, best.PhoneId, best.Id);

            item.SimCardId = best.Id;
            item.PhoneId = best.PhoneId;
            item.Status = QueueItemStatus.Assigned;
            item.PlannedAt = now;

            // Учитываем выдачу в "полёте", чтобы не превысить минуту на одной SIM.
            inFlight[best.Id] = inFlight.GetValueOrDefault(best.Id) + 1;
            assigned++;
        }

        if (assigned > 0 || skipped > 0)
            await db.SaveChangesAsync(ct);

        return assigned;
    }

    /// <summary>Снимает с очереди задачи, которым больше нельзя уходить. Возвращает их число.</summary>
    private async Task<int> RecheckConsentsAsync(
        ClinicHubDbContext db, IConsentChecker consents, List<SmsQueueItem> pending, DateTime now, CancellationToken ct)
    {
        var withRecipient = pending.Where(q => q.Recipient?.Request != null).ToList();
        var skipped = 0;
        var touchedRequests = new HashSet<int>();

        foreach (var group in withRecipient.GroupBy(q => q.Recipient!.Request!.Purpose))
        {
            var decisions = await consents.CheckManyAsync(
                group.Where(q => q.Recipient!.PatientId != null).Select(q => q.Recipient!.PatientId!.Value).Distinct().ToList(),
                group.Key, ConsentChannel.Sms, now, ct);

            foreach (var item in group)
            {
                var rec = item.Recipient!;
                var decision = rec.PatientId is int pid ? decisions[pid] : ConsentRules.ForUnknownRecipient(group.Key);
                if (decision.Allowed)
                    continue;

                item.Status = QueueItemStatus.Failed;
                item.LastError = "Не отправлено: " + decision.Reason;
                Skip(rec, decision.Reason);
                touchedRequests.Add(rec.RequestId);
                skipped++;
                _logger.LogInformation("Задача #{Id} снята перед отправкой — {Reason}", item.Id, decision.Reason);
            }
        }

        if (touchedRequests.Count > 0)
        {
            var ids = touchedRequests.ToList();
            var requests = await db.SmsRequests.Include(r => r.Recipients)
                .Where(r => ids.Contains(r.Id)).ToListAsync(ct);
            foreach (var request in requests)
            {
                if (RequestOutcome.Final(request.Recipients) is { } final)
                    request.Status = final;
            }
        }
        return skipped;
    }

    /// <summary>Обработка результата из SmsAgent (идемпотентно по taskId=QueueItem.Id).</summary>
    public async Task<QueueItemStatus> ProcessTaskResultAsync(
        long queueId, bool success, int reportedSegments, string? error, CancellationToken ct)
    {
        await using var db = _scopeFactory.CreateScope().ServiceProvider
            .GetRequiredService<ClinicHubDbContext>();

        var item = await db.SmsQueue
            .Include(q => q.Recipient)
            .ThenInclude(r => r!.Request)
            .Include(q => q.SimCard)
            .FirstOrDefaultAsync(q => q.Id == queueId, ct)
            ?? throw new KeyNotFoundException($"Задача {queueId} не найдена");

        // Идемпотентность: повторный отчёт по уже завершённой задаче игнорируем.
        if (item.Status is QueueItemStatus.Sent or QueueItemStatus.Failed)
            return item.Status;

        var now = DateTime.UtcNow;
        item.Attempts++;

        if (success)
        {
            item.Status = QueueItemStatus.Sent;

            if (item.Recipient != null)
            {
                item.Recipient.Status = RecipientStatus.Sent;
                item.Recipient.SentAt = now;

                var segments = reportedSegments > 0 ? reportedSegments : Math.Max(item.Recipient.SmsSegments, 1);

                // Списываем фактическое число сегментов с остатка SIM.
                if (item.SimCard != null)
                {
                    var sim = await db.SimCards.FirstAsync(s => s.Id == item.SimCard.Id, ct);
                    var change = -Math.Min(segments, sim.Balance);
                    sim.Balance += change;
                    db.BalanceLogs.Add(new BalanceLog
                    {
                        SimCardId = sim.Id,
                        Change = change,
                        Reason = "Отправка SMS",
                        RequestId = item.Recipient.RequestId,
                        BalanceAfter = sim.Balance,
                        CreatedAt = now,
                    });

                    // Успех — сбрасываем счётчик ошибок и запоминаем текст (для антидубля).
                    sim.ConsecutiveFailures = 0;
                    sim.LastMessageHash = item.Recipient.MessageText.GetHashCode().ToString();
                    sim.LastMessageAt = now;
                }

                db.SmsLogs.Add(new SmsLog
                {
                    QueueId = item.Id,
                    SimCardId = item.SimCardId,
                    PhoneId = item.PhoneId,
                    Segments = segments,
                    Status = SmsStatus.Sent,
                    SentAt = now,
                });
            }
        }
        else
        {
            item.LastError = error;

            // Серия ошибок подряд с одной SIM — верный признак, что её вот-вот забанят или
            // она уже нерабочая (кончился баланс у мобильного оператора и т.п.) — ставим на паузу.
            if (item.SimCardId is int failedSimId)
            {
                var failedSim = await db.SimCards.FirstOrDefaultAsync(s => s.Id == failedSimId, ct);
                if (failedSim != null)
                {
                    failedSim.ConsecutiveFailures++;
                    if (failedSim.ConsecutiveFailures >= 3)
                    {
                        failedSim.PausedUntil = now.AddMinutes(30);
                        _logger.LogWarning(
                            "SIM {Id} приостановлена на 30 минут: {N} ошибок подряд",
                            failedSim.Id, failedSim.ConsecutiveFailures);
                    }
                }
            }

            var maxAttempts = await db.AppSettings
                .Where(s => s.Key == "MaxAttempts")
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct) ?? "3";

            var max = int.TryParse(maxAttempts, out var m) ? m : 3;

            if (item.Attempts >= max)
            {
                item.Status = QueueItemStatus.Failed;
                if (item.Recipient != null)
                    item.Recipient.Status = RecipientStatus.Failed;

                db.SmsLogs.Add(new SmsLog
                {
                    QueueId = item.Id,
                    SimCardId = item.SimCardId,
                    PhoneId = item.PhoneId,
                    Segments = Math.Max(item.Recipient?.SmsSegments ?? 1, 1),
                    Status = SmsStatus.Failed,
                    SentAt = now,
                    ResultMessage = error ?? "Неизвестная ошибка",
                });
            }
            else
            {
                // Повторная попытка: возвращаем в пул для нового распределения.
                item.Status = QueueItemStatus.Pending;
                item.SimCardId = null;
                item.PhoneId = null;
            }
        }

        await db.SaveChangesAsync(ct);
        return item.Status;
    }
}