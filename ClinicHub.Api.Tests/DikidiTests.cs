using System.Text.Json.Nodes;
using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Catalog;
using ClinicHub.Api.Modules.Integrations.Dikidi;
using ClinicHub.Api.Modules.Shared;
using ClinicHub.Api.Modules.Visits;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicHub.Api.Tests;

/// <summary>Разбор ответа журнала Dikidi. Ответ выдуман в формате журнала, люди и телефоны ненастоящие.</summary>
public sealed class DikidiParserTests
{
    private const string Journal = """
        {
          "masters": {
            "info": { "data": {
              "101": { "name": "Иванова Анна", "post": "Косметолог" },
              "102": { "title": "Петрова Вера" }
            } },
            "records": { "data": {
              "101": {
                "2026-10-21": {
                  "5001": { "id": 5001, "begin": "2026-10-21 14:30:00", "end": "2026-10-21 15:15:00",
                            "info": { "client_name": "Тестова Ольга", "client_phone": "+7 (999) 000-00-01", "status_id": "1",
                                      "services": [ { "title": "Ботокс зона", "cost": "9000" }, { "title": "Консультация" } ] } },
                  "5002": { "id": "5002", "begin": "2026-10-21 16:00:00",
                            "info": { "client": { "name": "Пробный Иван", "phone": "89990000002" }, "status_id": 4,
                                      "services_title": "Уход, Пилинг" } },
                  "5003": { "id": "5003", "begin": "2026-10-21 13:00:00", "type": "break", "info": { "comment": "Обед" } }
                }
              },
              "102": { "22.10.2026": { "5004": { "begin": "10:00", "item_id": "102",
                                                "info": { "client_name": "Выдуманная Мария", "client_phone": "9990000003", "status_id": 5 } } } }
            } }
          }
        }
        """;

    [Fact]
    public void ReadsRecordsMastersServicesAndStatuses()
    {
        var journal = DikidiJournalParser.Parse(JsonNode.Parse(Journal));

        Assert.Equal(3, journal.Records.Count);
        Assert.Equal(1, journal.SkippedBlocks);

        var a = journal.Records.Single(r => r.Id == "5001");
        Assert.Equal("Иванова Анна", a.MasterName);
        Assert.Equal(new DateOnly(2026, 10, 21), a.Date);
        Assert.Equal(new TimeOnly(14, 30), a.Start);
        Assert.Equal("+7 (999) 000-00-01", a.ClientPhone);
        Assert.Equal(new[] { "Ботокс зона", "Консультация" }, a.Services);
        Assert.Equal(DikidiRecordState.Active, a.State);

        var b = journal.Records.Single(r => r.Id == "5002");
        Assert.Equal("Пробный Иван", b.ClientName);
        Assert.Equal("89990000002", b.ClientPhone);
        Assert.Equal(new[] { "Уход", "Пилинг" }, b.Services);
        Assert.Equal(DikidiRecordState.Cancelled, b.State);

        var c = journal.Records.Single(r => r.Id == "5004");
        Assert.Equal("Петрова Вера", c.MasterName);
        Assert.Equal(new DateOnly(2026, 10, 22), c.Date);
        Assert.Equal(new TimeOnly(10, 0), c.Start);
        Assert.Equal(DikidiRecordState.Cancelled, c.State);
    }

    [Fact]
    public void EmptyJournalComesAsPhpEmptyArray()
    {
        var journal = DikidiJournalParser.Parse(JsonNode.Parse("""{ "masters": { "info": [], "records": { "data": [] } } }"""));
        Assert.Empty(journal.Records);
    }

    [Theory]
    [InlineData("""{ "masters": { "info": {} } }""", "masters.records")]
    [InlineData("""{ "something": 1 }""", "masters")]
    [InlineData("""{ "error": 1, "message": "Недостаточно данных" }""", "Недостаточно данных")]
    [InlineData("""[1, 2]""", "не JSON-объект")]
    public void ChangedFormatFailsLoudly(string json, string expected)
    {
        var ex = Assert.Throws<DikidiException>(() => DikidiJournalParser.Parse(JsonNode.Parse(json)));
        Assert.Contains(expected, ex.Message);
    }
}

/// <summary>Перенос записей в визиты (Issue #15). Вся база в памяти, данные вымышленные.</summary>
public sealed class DikidiSyncTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 21);
    private static readonly DateOnly From = new(2026, 10, 19);
    private static readonly DateOnly To = new(2026, 11, 19);

    private readonly ClinicHubDbContext _db;
    private readonly FakeClock _clock;
    private readonly FakeDikidi _client = new();
    private readonly DikidiSyncState _state = new() { Configured = true };
    private readonly DikidiSync _sync;
    private readonly Patient _known;

    public DikidiSyncTests()
    {
        _db = new ClinicHubDbContext(new DbContextOptionsBuilder<ClinicHubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _clock = new FakeClock(DateTime.UtcNow);
        _clock.SetMoscow(2026, 10, 20, 12, 0);
        _sync = new DikidiSync(_db, _client, new DikidiOptions(), _state, _clock, NullLogger<DikidiSync>.Instance);

        _db.Services.AddRange(
            new Service { Id = "botox", Name = "Ботулинотерапия" },
            new Service { Id = "care", Name = "Уход за лицом" });
        _db.Doctors.Add(new Doctor { Id = "ivanova", Name = "Иванова Анна" });
        _db.ExternalServiceMaps.AddRange(
            new ExternalServiceMap { Source = VisitSource.Dikidi, ExternalKey = "Ботокс зона", ServiceId = "botox" },
            new ExternalServiceMap { Source = VisitSource.Dikidi, ExternalKey = "Уход", ServiceId = "care" });
        // Телефон старого пациента введён «как попало» — сопоставление должно его понять.
        _known = new Patient { FullName = "Тестова Ольга", Phone = "8 (999) 000-00-01" };
        _db.Patients.Add(_known);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private static DikidiRecord Rec(
        string id, string? phone = "+79990000001", int hour = 14, string[]? services = null,
        DikidiRecordState state = DikidiRecordState.Active, string name = "Тестова Ольга", DateOnly? date = null) =>
        new(id, "101", "Иванова Анна", date ?? Day, new TimeOnly(hour, 30), name, phone, services ?? ["Ботокс зона"], state);

    private Task<DikidiSyncResult> Apply(params DikidiRecord[] records) =>
        _sync.ApplyAsync(new DikidiJournal(records, 0), From, To, CancellationToken.None);

    private Visit Visit(string externalId) =>
        _db.Visits.Include(v => v.Services).AsNoTracking().Single(v => v.ExternalId == externalId);

    [Fact]
    public async Task NewRecordBecomesPlannedVisitOfKnownPatient()
    {
        var result = await Apply(Rec("1", services: ["Ботокс зона", "Чистка"]));

        var v = Visit("1");
        Assert.Equal(VisitStatus.Planned, v.Status);
        Assert.Equal(VisitSource.Dikidi, v.Source);
        Assert.Equal(_known.Id, v.PatientId);
        Assert.Equal(MoscowTime.ToUtc(Day, new TimeOnly(14, 30)), v.StartAt);
        Assert.Equal("ivanova", v.DoctorId);
        Assert.Equal("Иванова Анна", v.DoctorName);
        var service = Assert.Single(v.Services);
        Assert.Equal(("botox", "Ботулинотерапия"), (service.ServiceId, service.ServiceName));

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.PatientsCreated);
        var unmapped = Assert.Single(result.Unmapped);
        Assert.Equal(("Чистка", 1), (unmapped.Name, unmapped.Records));
    }

    [Fact]
    public async Task UnknownPhoneCreatesOneDikidiPatient()
    {
        var result = await Apply(
            Rec("1", phone: "8 999 000 00 09", name: "Новая Пациентка"),
            Rec("2", phone: "+79990000009", name: "Новая Пациентка", hour: 16));

        var created = _db.Patients.AsNoTracking().Single(p => p.Phone == "+79990000009");
        Assert.Equal(PatientSource.Dikidi, created.Source);
        Assert.Equal("Новая Пациентка", created.FullName);
        Assert.Equal(1, result.PatientsCreated);
        Assert.All(_db.Visits.AsNoTracking().ToList(), v => Assert.Equal(created.Id, v.PatientId));
    }

    [Fact]
    public async Task RepeatedSyncUpdatesInsteadOfDuplicating()
    {
        await Apply(Rec("1", hour: 14, services: ["Ботокс зона"]));
        var second = await Apply(Rec("1", hour: 17, services: ["Уход"]));
        var third = await Apply(Rec("1", hour: 17, services: ["Уход"]));

        var v = Visit("1");
        Assert.Single(_db.Visits);
        Assert.Equal(MoscowTime.ToUtc(Day, new TimeOnly(17, 30)), v.StartAt);
        Assert.Equal("care", Assert.Single(v.Services).ServiceId);
        Assert.Equal(1, second.Updated);
        Assert.Equal(0, third.Updated);
        Assert.Equal(0, third.Created);
    }

    [Fact]
    public async Task CancelledInDikidiCancelsPlannedVisit()
    {
        await Apply(Rec("1"));
        var result = await Apply(Rec("1", state: DikidiRecordState.Cancelled));

        var v = Visit("1");
        Assert.Equal(VisitStatus.Cancelled, v.Status);
        Assert.Equal(DikidiSync.CancelledComment, v.Comment);
        Assert.Equal(1, result.Cancelled);
    }

    [Fact]
    public async Task CancelledRecordWithoutVisitIsNotImported()
    {
        await Apply(Rec("1", state: DikidiRecordState.Cancelled));
        Assert.Empty(_db.Visits);
    }

    [Fact]
    public async Task VisitConfirmedByAdminIsNotTouched()
    {
        await Apply(Rec("1"));
        var tracked = _db.Visits.Single(v => v.ExternalId == "1");
        tracked.Status = VisitStatus.Completed;
        tracked.ConfirmedAt = _clock.GetUtcNow().UtcDateTime;
        _db.SaveChanges();

        var result = await Apply(Rec("1", hour: 18, state: DikidiRecordState.Cancelled));

        var v = Visit("1");
        Assert.Equal(VisitStatus.Completed, v.Status);
        Assert.Equal(MoscowTime.ToUtc(Day, new TimeOnly(14, 30)), v.StartAt);
        Assert.Equal(1, result.Untouched);
    }

    [Fact]
    public async Task VanishedRecordIsCancelledAndRevivedWhenItReturns()
    {
        await Apply(Rec("1"), Rec("2", hour: 16));

        var gone = await Apply(Rec("2", hour: 16));
        Assert.Equal(VisitStatus.Cancelled, Visit("1").Status);
        Assert.Equal(DikidiSync.VanishedComment, Visit("1").Comment);
        Assert.Equal(1, gone.Cancelled);

        await Apply(Rec("1"), Rec("2", hour: 16));
        Assert.Equal(VisitStatus.Planned, Visit("1").Status);
        Assert.Null(Visit("1").Comment);
    }

    [Fact]
    public async Task EmptyJournalCancelsNothing()
    {
        await Apply(Rec("1"));
        var result = await Apply();

        Assert.Equal(VisitStatus.Planned, Visit("1").Status);
        Assert.Equal(0, result.Cancelled);
    }

    [Fact]
    public async Task RecordWithoutMobilePhoneIsSkipped()
    {
        var result = await Apply(Rec("1", phone: null), Rec("2", phone: "+7 812 000-00-00"));

        Assert.Empty(_db.Visits);
        Assert.Equal(2, result.SkippedNoPhone);
    }

    [Fact]
    public async Task FailureIsRecordedInStateAndRethrown()
    {
        _client.Error = new DikidiException("Dikidi не принял номер или пароль учётки адаптера.");

        await Assert.ThrowsAsync<DikidiException>(() => _sync.RunAsync(CancellationToken.None));
        Assert.Equal("Dikidi не принял номер или пароль учётки адаптера.", _state.LastError);
        Assert.Null(_state.LastSuccessAt);

        _client.Error = null;
        _client.Journal = new DikidiJournal([Rec("1")], 0);
        var result = await _sync.RunAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(_state.LastError);
        Assert.NotNull(_state.LastSuccessAt);
        Assert.Equal(new DateOnly(2026, 10, 19), _client.From);
        Assert.Equal(new DateOnly(2026, 11, 19), _client.To);
    }

    private sealed class FakeDikidi : IDikidiClient
    {
        public Exception? Error { get; set; }
        public DikidiJournal Journal { get; set; } = new([], 0);
        public DateOnly From { get; private set; }
        public DateOnly To { get; private set; }

        public Task<DikidiJournal> GetJournalAsync(DateOnly from, DateOnly to, CancellationToken ct)
        {
            (From, To) = (from, to);
            return Error is null ? Task.FromResult(Journal) : Task.FromException<DikidiJournal>(Error);
        }
    }
}
