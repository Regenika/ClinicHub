using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Shared;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Consents;

/// <summary>Решение: можно ли отправить, и причина человеческими словами (для StatusReason, SkipReason и журнала).</summary>
public readonly record struct ConsentDecision(bool Allowed, string Reason)
{
    public static ConsentDecision Allow(string reason) => new(true, reason);
    public static ConsentDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Единственное место, где решается, можно ли писать пациенту (Issue #34). Обходить нельзя даже «для теста»
/// на реальных номерах (CLAUDE.md). Все отправки — заявки, авто-правила, напоминания — спрашивают здесь.
/// </summary>
public interface IConsentChecker
{
    /// <param name="patientId">null — получатель не из базы пациентов (номер вписан вручную).</param>
    Task<ConsentDecision> CheckAsync(int? patientId, MessagePurpose purpose, ConsentChannel channel, DateTime atUtc, CancellationToken ct);

    /// <summary>То же для многих пациентов сразу (массовая рассылка). В ответе есть каждый запрошенный id.</summary>
    Task<IReadOnlyDictionary<int, ConsentDecision>> CheckManyAsync(
        IReadOnlyCollection<int> patientIds, MessagePurpose purpose, ConsentChannel channel, DateTime atUtc, CancellationToken ct);
}

/// <summary>Правила без базы данных — их и проверяют юнит-тесты.</summary>
public static class ConsentRules
{
    /// <summary>Каналы, по которым показывается текущее состояние (Any — не канал, а «на все сразу»).</summary>
    public static readonly ConsentChannel[] Channels =
        [ConsentChannel.Sms, ConsentChannel.Telegram, ConsentChannel.Vk, ConsentChannel.Max, ConsentChannel.Email];

    /// <summary>
    /// Действующая запись для цели и канала: последняя по времени (At, затем Id) среди записей этого канала и Any.
    /// Так отзыв на один канал перекрывает более раннее согласие на Any, а отзыв на Any — все прежние согласия.
    /// </summary>
    public static ConsentRecord? Effective(
        IEnumerable<ConsentRecord> records, MessagePurpose purpose, ConsentChannel channel, DateTime atUtc) =>
        records
            .Where(r => r.Purpose == purpose && r.At <= atUtc
                        && (r.Channel == channel || r.Channel == ConsentChannel.Any || channel == ConsentChannel.Any))
            .OrderByDescending(r => r.At).ThenByDescending(r => r.Id)
            .FirstOrDefault();

    public static ConsentDecision Evaluate(
        Patient? patient, IEnumerable<ConsentRecord> records, MessagePurpose purpose, ConsentChannel channel, DateTime atUtc)
    {
        if (patient is null)
            return ConsentDecision.Deny("пациент удалён");

        // Отписка останавливает всё, в том числе сервисные сообщения.
        if (!patient.IsSubscribed)
            return ConsentDecision.Deny("пациент отписан от рассылок");

        // Сервисные (продолжение лечения по визиту) — без рекламного согласия (ARCHITECTURE §3.2).
        if (purpose == MessagePurpose.Service)
            return ConsentDecision.Allow("сервисное сообщение");

        var effective = Effective(records, purpose, channel, atUtc);
        var where = ChannelName(channel);
        if (effective is null)
            return ConsentDecision.Deny($"нет согласия на рекламу {where}");
        return effective.Granted
            ? ConsentDecision.Allow($"согласие на рекламу {where} от {MoscowTime.ToMoscow(effective.At):dd.MM.yyyy}")
            : ConsentDecision.Deny($"согласие на рекламу {where} отозвано {MoscowTime.ToMoscow(effective.At):dd.MM.yyyy}");
    }

    /// <summary>Номер вписан вручную, пациента в базе нет: сервисное можно, рекламу — нет (согласие не проверить).</summary>
    public static ConsentDecision ForUnknownRecipient(MessagePurpose purpose) =>
        purpose == MessagePurpose.Service
            ? ConsentDecision.Allow("сервисное сообщение")
            : ConsentDecision.Deny("получатель не из базы пациентов — согласие на рекламу не проверить");

    public static string ChannelName(ConsentChannel channel) => channel switch
    {
        ConsentChannel.Sms => "по SMS",
        ConsentChannel.Telegram => "в Telegram",
        ConsentChannel.Vk => "во ВКонтакте",
        ConsentChannel.Max => "в MAX",
        ConsentChannel.Email => "по почте",
        _ => "по всем каналам",
    };
}

public sealed class ConsentChecker(ClinicHubDbContext db) : IConsentChecker
{
    public async Task<ConsentDecision> CheckAsync(
        int? patientId, MessagePurpose purpose, ConsentChannel channel, DateTime atUtc, CancellationToken ct)
    {
        if (patientId is not int id)
            return ConsentRules.ForUnknownRecipient(purpose);
        var many = await CheckManyAsync([id], purpose, channel, atUtc, ct);
        return many[id];
    }

    public async Task<IReadOnlyDictionary<int, ConsentDecision>> CheckManyAsync(
        IReadOnlyCollection<int> patientIds, MessagePurpose purpose, ConsentChannel channel, DateTime atUtc, CancellationToken ct)
    {
        var result = new Dictionary<int, ConsentDecision>();
        // Кусками: длинный список в IN (…) неудобен любой СУБД.
        foreach (var chunkArray in patientIds.Distinct().Chunk(500))
        {
            // List, а не массив: Contains у списка EF переводит в SQL одинаково во всех версиях.
            var chunk = chunkArray.ToList();
            var patients = await db.Patients.AsNoTracking()
                .Where(p => chunk.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, ct);

            var records = purpose == MessagePurpose.Marketing
                ? (await db.ConsentRecords.AsNoTracking()
                        .Where(r => chunk.Contains(r.PatientId) && r.Purpose == purpose && r.At <= atUtc)
                        .ToListAsync(ct))
                    .ToLookup(r => r.PatientId)
                : Enumerable.Empty<ConsentRecord>().ToLookup(r => r.PatientId);

            foreach (var id in chunk)
                result[id] = ConsentRules.Evaluate(patients.GetValueOrDefault(id), records[id], purpose, channel, atUtc);
        }
        return result;
    }
}
