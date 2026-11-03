using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Consents;
using ClinicHub.Api.Services;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicHub.Api.Tests;

/// <summary>Правила согласий (Issue #34) без базы. Пациенты вымышленные.</summary>
public sealed class ConsentRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 26, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Patient Subscribed = new() { Id = 1, FullName = "Тестова Анна", Phone = "+79990000001", IsSubscribed = true };

    private static long _id;

    private static ConsentRecord Rec(ConsentChannel channel, bool granted, int daysAgo, MessagePurpose purpose = MessagePurpose.Marketing) => new()
    {
        Id = ++_id, PatientId = 1, Purpose = purpose, Channel = channel, Granted = granted,
        Source = ConsentSource.PaperForm, At = Now.AddDays(-daysAgo),
    };

    private static ConsentDecision Marketing(ConsentChannel channel, params ConsentRecord[] records) =>
        ConsentRules.Evaluate(Subscribed, records, MessagePurpose.Marketing, channel, Now);

    [Fact]
    public void БезЗаписейРекламыНет_СервисноеМожно()
    {
        var marketing = Marketing(ConsentChannel.Sms);
        Assert.False(marketing.Allowed);
        Assert.Equal("нет согласия на рекламу по SMS", marketing.Reason);

        Assert.True(ConsentRules.Evaluate(Subscribed, [], MessagePurpose.Service, ConsentChannel.Sms, Now).Allowed);
    }

    [Fact]
    public void СогласиеНаВсеКаналыДействуетДляSms()
    {
        var d = Marketing(ConsentChannel.Sms, Rec(ConsentChannel.Any, true, 10));
        Assert.True(d.Allowed);
        Assert.StartsWith("согласие на рекламу по SMS от", d.Reason);
    }

    [Fact]
    public void СогласиеИСледомОтзыв()
    {
        var d = Marketing(ConsentChannel.Sms, Rec(ConsentChannel.Sms, true, 10), Rec(ConsentChannel.Sms, false, 2));
        Assert.False(d.Allowed);
        Assert.Contains("отозвано", d.Reason);
    }

    [Fact]
    public void ОтзывНаОдинКаналПослеСогласияНаВсеПерекрываетТолькоЭтотКанал()
    {
        ConsentRecord[] records = [Rec(ConsentChannel.Any, true, 10), Rec(ConsentChannel.Sms, false, 2)];
        Assert.False(Marketing(ConsentChannel.Sms, records).Allowed);
        Assert.True(Marketing(ConsentChannel.Telegram, records).Allowed);
    }

    [Fact]
    public void ОтзывНаВсеКаналыПерекрываетБолееРанниеСогласия()
    {
        ConsentRecord[] records = [Rec(ConsentChannel.Sms, true, 10), Rec(ConsentChannel.Any, false, 2)];
        Assert.False(Marketing(ConsentChannel.Sms, records).Allowed);
    }

    [Fact]
    public void НовоеСогласиеПослеОтзываСноваРазрешает()
    {
        Assert.True(Marketing(ConsentChannel.Sms, Rec(ConsentChannel.Any, false, 10), Rec(ConsentChannel.Sms, true, 1)).Allowed);
    }

    [Fact]
    public void СогласиеИзБудущегоЕщёНеДействует()
    {
        Assert.False(Marketing(ConsentChannel.Sms, Rec(ConsentChannel.Sms, true, -1)).Allowed);
    }

    [Fact]
    public void СервисноеСогласиеНеДаётРекламного()
    {
        Assert.False(Marketing(ConsentChannel.Sms, Rec(ConsentChannel.Any, true, 5, MessagePurpose.Service)).Allowed);
    }

    [Fact]
    public void ОтписанномуНельзяНичего()
    {
        var unsubscribed = new Patient { Id = 1, IsSubscribed = false };
        ConsentRecord[] records = [Rec(ConsentChannel.Any, true, 10)];
        foreach (var purpose in new[] { MessagePurpose.Service, MessagePurpose.Marketing })
        {
            var d = ConsentRules.Evaluate(unsubscribed, records, purpose, ConsentChannel.Sms, Now);
            Assert.False(d.Allowed);
            Assert.Equal("пациент отписан от рассылок", d.Reason);
        }
    }

    [Fact]
    public void НомерНеИзБазы_СервисноеМожноРекламуНет()
    {
        Assert.True(ConsentRules.ForUnknownRecipient(MessagePurpose.Service).Allowed);
        Assert.False(ConsentRules.ForUnknownRecipient(MessagePurpose.Marketing).Allowed);
    }
}

/// <summary>Журнал в базе, планировщик и отправка с проверкой согласий. База в памяти, номера вымышленные.</summary>
public sealed class ConsentPipelineTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly ClinicHubDbContext _db;
    private readonly Patient _agreed;
    private readonly Patient _silent;

    public ConsentPipelineTests()
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ClinicHubDbContext>(o => o.UseInMemoryDatabase(name));
        services.AddScoped<IConsentChecker, ConsentChecker>();
        _services = services.BuildServiceProvider();
        _db = _services.CreateScope().ServiceProvider.GetRequiredService<ClinicHubDbContext>();

        _agreed = new Patient { FullName = "Согласна Анна", Phone = "+79990000011", IsSubscribed = true };
        _silent = new Patient { FullName = "Молчанов Борис", Phone = "+79990000012", IsSubscribed = true };
        _db.Patients.AddRange(_agreed, _silent);
        _db.SaveChanges();
        _db.ConsentRecords.Add(new ConsentRecord
        {
            PatientId = _agreed.Id, Purpose = MessagePurpose.Marketing, Channel = ConsentChannel.Sms, Granted = true,
            Source = ConsentSource.PaperForm, EvidenceRef = "форма 17", At = DateTime.UtcNow.AddDays(-3),
        });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _services.Dispose();
    }

    private ClinicHubDbContext Fresh() => _services.CreateScope().ServiceProvider.GetRequiredService<ClinicHubDbContext>();

    private SchedulerService Scheduler() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<SchedulerService>.Instance);

    private SmsRequest AddRequest(MessagePurpose purpose, params Patient[] patients)
    {
        var request = new SmsRequest
        {
            Type = patients.Length > 1 ? RequestType.Mass : RequestType.Personal, Purpose = purpose, Title = "тест",
            Status = RequestStatus.Approved, SendMode = SendMode.Now, TextTemplate = "Текст",
        };
        foreach (var p in patients)
            request.Recipients.Add(new RequestRecipient
            {
                PatientId = p.Id, Phone = p.Phone, MessageText = "Текст", SmsSegments = 1, SendAfter = DateTime.UtcNow.AddMinutes(-1),
            });
        _db.SmsRequests.Add(request);
        _db.SaveChanges();
        return request;
    }

    [Fact]
    public async Task ПроверкаМногихВозвращаетКаждогоПациентаДажеУдалённого()
    {
        var checker = new ConsentChecker(_db);
        var result = await checker.CheckManyAsync([_agreed.Id, _silent.Id, 999], MessagePurpose.Marketing, ConsentChannel.Sms, DateTime.UtcNow, default);

        Assert.True(result[_agreed.Id].Allowed);
        Assert.False(result[_silent.Id].Allowed);
        Assert.Equal("пациент удалён", result[999].Reason);
    }

    [Fact]
    public void ЖурналНельзяМенятьИУдалять()
    {
        var record = _db.ConsentRecords.Single();
        record.Granted = false;
        Assert.Throws<InvalidOperationException>(() => _db.SaveChanges());
        _db.Entry(record).State = EntityState.Unchanged;

        _db.ConsentRecords.Remove(record);
        Assert.Throws<InvalidOperationException>(() => _db.SaveChanges());
    }

    [Fact]
    public void ЖурналУходитВместеСУдалённымПациентом()
    {
        _ = _db.ConsentRecords.ToList();
        _db.Patients.Remove(_agreed);
        _db.SaveChanges();

        Assert.Empty(Fresh().ConsentRecords);
    }

    [Fact]
    public async Task РекламнаяРассылкаИдётТолькоТемКтоСогласен()
    {
        var request = AddRequest(MessagePurpose.Marketing, _agreed, _silent);

        await Scheduler().PlanAsync(default);

        var db = Fresh();
        var recs = db.RequestRecipients.Where(r => r.RequestId == request.Id).ToList();
        var agreed = recs.Single(r => r.PatientId == _agreed.Id);
        var silent = recs.Single(r => r.PatientId == _silent.Id);
        Assert.Equal(RecipientStatus.Scheduled, agreed.Status);
        Assert.Equal(RecipientStatus.Skipped, silent.Status);
        Assert.Equal("нет согласия на рекламу по SMS", silent.SkipReason);
        Assert.Equal(agreed.Id, Assert.Single(db.SmsQueue).RecipientId);
        Assert.Equal(RequestStatus.Scheduled, db.SmsRequests.Single(r => r.Id == request.Id).Status);
    }

    [Fact]
    public async Task СервиснаяРассылкаНеТребуетРекламногоСогласия()
    {
        AddRequest(MessagePurpose.Service, _silent);

        await Scheduler().PlanAsync(default);

        Assert.Equal(RecipientStatus.Scheduled, Fresh().RequestRecipients.Single().Status);
    }

    [Fact]
    public async Task ЕслиПропустилиВсехЗаявкаЗавершается()
    {
        var request = AddRequest(MessagePurpose.Marketing, _silent);

        await Scheduler().PlanAsync(default);

        var db = Fresh();
        Assert.Equal(RequestStatus.Failed, db.SmsRequests.Single(r => r.Id == request.Id).Status);
        Assert.Empty(db.SmsQueue);
    }

    [Fact]
    public async Task ОтзывПокаSmsЖдалаВОчередиСнимаетЕёПередОтправкой()
    {
        // Телефон на связи и окно отправки на весь день — иначе распределение не дойдёт до выдачи.
        _db.RateConfigs.Add(new RateConfig { Name = "тест", IsActive = true, SendWindowFrom = TimeSpan.Zero, SendWindowTo = new TimeSpan(23, 59, 59) });
        var phone = new Phone { PhoneNumber = "+79990000099", IsActive = true, LastSeenAt = DateTime.UtcNow };
        phone.SimCards.Add(new SimCard { Number = "+79990000099", Balance = 100, LimitPerMinute = 10, LimitPerHour = 100, LimitPerDay = 100 });
        _db.Phones.Add(phone);
        _db.SaveChanges();

        var request = AddRequest(MessagePurpose.Marketing, _agreed);
        await Scheduler().PlanAsync(default);
        Assert.Single(Fresh().SmsQueue);

        _db.ConsentRecords.Add(new ConsentRecord
        {
            PatientId = _agreed.Id, Purpose = MessagePurpose.Marketing, Channel = ConsentChannel.Any, Granted = false,
            Source = ConsentSource.Sms, At = DateTime.UtcNow,
        });
        _db.SaveChanges();

        Assert.Equal(0, await Scheduler().DistributeAsync(default));

        var db = Fresh();
        var item = db.SmsQueue.Single();
        Assert.Equal(QueueItemStatus.Failed, item.Status);
        Assert.Contains("отозвано", item.LastError);
        var rec = db.RequestRecipients.Single(r => r.RequestId == request.Id);
        Assert.Equal(RecipientStatus.Skipped, rec.Status);
        Assert.Contains("отозвано", rec.SkipReason);
        Assert.Equal(RequestStatus.Failed, db.SmsRequests.Single(r => r.Id == request.Id).Status);
    }

    [Fact]
    public void ИтогЗаявки()
    {
        RequestRecipient R(RecipientStatus s) => new() { Status = s };
        Assert.Null(RequestOutcome.Final([R(RecipientStatus.Sent), R(RecipientStatus.Scheduled)]));
        Assert.Equal(RequestStatus.Sent, RequestOutcome.Final([R(RecipientStatus.Sent), R(RecipientStatus.Sent)]));
        Assert.Equal(RequestStatus.Partial, RequestOutcome.Final([R(RecipientStatus.Sent), R(RecipientStatus.Skipped)]));
        Assert.Equal(RequestStatus.Failed, RequestOutcome.Final([R(RecipientStatus.Failed), R(RecipientStatus.Skipped)]));
    }
}
