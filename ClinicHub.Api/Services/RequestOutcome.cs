using ClinicHub.Core.Entities;

namespace ClinicHub.Api.Services;

/// <summary>Итоговый статус заявки по её получателям — одно правило для планировщика и отчётов телефона.</summary>
public static class RequestOutcome
{
    /// <summary>
    /// null — ещё не все получатели в конечном статусе. Иначе: все отправлены → Sent; не отправлено никому → Failed;
    /// часть отправлена, часть нет (ошибка или пропуск без согласия) → Partial.
    /// </summary>
    public static RequestStatus? Final(IReadOnlyCollection<RequestRecipient> recipients)
    {
        if (recipients.Count == 0)
            return null;
        if (recipients.Any(r => r.Status is RecipientStatus.Pending or RecipientStatus.Scheduled or RecipientStatus.Sending))
            return null;

        var sent = recipients.Count(r => r.Status == RecipientStatus.Sent);
        return sent == recipients.Count ? RequestStatus.Sent
            : sent == 0 ? RequestStatus.Failed
            : RequestStatus.Partial;
    }
}
