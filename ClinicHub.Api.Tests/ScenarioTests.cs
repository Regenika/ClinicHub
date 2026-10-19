using ClinicHub.Api.Data;
using ClinicHub.Api.Modules.Catalog;
using ClinicHub.Api.Modules.Messaging.Scenarios;
using ClinicHub.Api.Modules.Shared;
using ClinicHub.Api.Modules.Visits;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClinicHub.Api.Tests;

/// <summary>Часы, которые показывают то, что скажет тест.</summary>
internal sealed class FakeClock(DateTime utc) : TimeProvider
{
    public DateTime Utc { get; set; } = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    public override DateTimeOffset GetUtcNow() => new(Utc);

    /// <summary>Поставить часы на дату и время по Москве.</summary>
    public void SetMoscow(int y, int m, int d, int h, int min) => Utc = MoscowTime.ToUtc(new DateOnly(y, m, d), new TimeOnly(h, min));
}

/// <summary>Планировщик и отправка напоминаний (Issue #14, ADR-0008 §4–5). Все пациенты вымышленные.</summary>
public sealed class ScenarioTests : IDisposable
{
    private readonly ClinicHubDbContext _db;
    private readonly FakeClock _clock;
    private readonly ScenarioPlanner _planner;
    private readonly ReminderDispatcher _dispatcher;
    private readonly Patient _patient;

    public ScenarioTests()
    {
        _db = new ClinicHubDbContext(new DbContextOptionsBuilder<ClinicHubDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _clock = new FakeClock(DateTime.UtcNow);
        _clock.SetMoscow(2026, 10, 20, 12, 0);
        _planner = new ScenarioPlanner(_db, _clock, NullLogger<ScenarioPlanner>.Instance);
        _dispatcher = new ReminderDispatcher(_db, _clock, NullLogger<ReminderDispatcher>.Instance);

        _db.Services.AddRange(
            new Service { Id = "botox", Name = "Ботулинотерапия" },
            new Service { Id = "care", Name = "Уход" });
        _patient = new Patient { FullName = "Тестов Тест", Phone = "+79990000001", IsSubscribed = true };
        _db.Patients.Add(_patient);
        _db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private CareScenario AddScenario(
        ScenarioPurpose purpose = ScenarioPurpose.Service, string[]? triggerServices = null, CareScenarioStep[]? steps = null)
    {
        var s = new CareScenario { Name = "Контроль", Purpose = purpose, Trigger = ScenarioTrigger.VisitCompleted };
        foreach (var id in triggerServices ?? [])
            s.TriggerServices.Add(new CareScenarioService { ServiceId = id });
        s.Steps.AddRange(steps is { Length: > 0 } ? steps : [Step(14)]);
        _db.CareScenarios.Add(s);
        _db.SaveChanges();
        return s;
    }

    private static CareScenarioStep Step(int days, int hour = 11, bool cancelIfFuture = false, bool cancelIfVisitAfter = false,
        string? cancelIfService = null) => new()
    {
        Order = days, OffsetDays = days, SendTime = new TimeSpan(hour, 0, 0), Text = "{name}, пора на осмотр",
        CancelIfFutureVisit = cancelIfFuture, CancelIfVisitAfter = cancelIfVisitAfter, CancelIfServiceAfterId = cancelIfService,
    };

    private Visit AddVisit(DateTime startUtc, VisitStatus status, params string[] services)
    {
        var v = new Visit { PatientId = _patient.Id, StartAt = startUtc, Status = status, DoctorName = "" };
        foreach (var s in services)
            v.Services.Add(new VisitService { ServiceId = s, ServiceName = s });
        _db.Visits.Add(v);
        _db.SaveChanges();
        return v;
    }

    private PlannedReminder AddDue(ScenarioPurpose purpose = ScenarioPurpose.Service, CareScenarioStep? step = null, DateTime? eventAt = null)
    {
        var r = new PlannedReminder
        {
            PatientId = _patient.Id, Kind = ReminderKind.Manual, Purpose = purpose, Text = "Текст",
            DueAt = _clock.Utc.AddMinutes(-5), EventAt = eventAt ?? _clock.Utc.AddDays(-30), StepId = step?.Id,
        };
        _db.PlannedReminders.Add(r);
        _db.SaveChanges();
        return r;
    }

    private List<PlannedReminder> Reminders() => _db.PlannedReminders.AsNoTracking().OrderBy(r => r.Id).ToList();

    // ── Планирование ────────────────────────────────────────────────────────

    [Fact]
    public async Task Срок_считается_от_даты_визита_по_Москве_во_время_шага()
    {
        AddScenario(steps: [Step(14, hour: 11)]);
        // Визит 20.10 в 18:00 по Москве → напоминание 03.11 в 11:00 по Москве.
        var visitStart = MoscowTime.ToUtc(new DateOnly(2026, 10, 20), new TimeOnly(18, 0));
        var v = AddVisit(visitStart, VisitStatus.Completed, "botox");

        await _planner.PlanForVisitAsync(v.Id, _patient.Id, v.StartAt, ["botox"], default);

        var r = Assert.Single(Reminders());
        Assert.Equal(MoscowTime.ToUtc(new DateOnly(2026, 11, 3), new TimeOnly(11, 0)), r.DueAt);
        Assert.Equal("Тестов Тест, пора на осмотр", r.Text);
        Assert.Equal(ReminderStatus.Planned, r.Status);
    }

    [Fact]
    public async Task Новый_визит_перезапускает_цепочку_того_же_сценария()
    {
        AddScenario(steps: [Step(14), Step(28)]);
        var v1 = AddVisit(_clock.Utc.AddDays(-1), VisitStatus.Completed, "botox");
        await _planner.PlanForVisitAsync(v1.Id, _patient.Id, v1.StartAt, ["botox"], default);
        var v2 = AddVisit(_clock.Utc, VisitStatus.Completed, "botox");
        await _planner.PlanForVisitAsync(v2.Id, _patient.Id, v2.StartAt, ["botox"], default);

        var all = Reminders();
        Assert.Equal(4, all.Count);
        Assert.All(all.Where(r => r.TriggerVisitId == v1.Id), r =>
        {
            Assert.Equal(ReminderStatus.Cancelled, r.Status);
            Assert.Contains("перезапущена", r.StatusReason);
        });
        Assert.All(all.Where(r => r.TriggerVisitId == v2.Id), r => Assert.Equal(ReminderStatus.Planned, r.Status));
    }

    [Fact]
    public async Task Сценарий_срабатывает_только_на_свои_услуги()
    {
        AddScenario(triggerServices: ["botox"]);
        var v = AddVisit(_clock.Utc, VisitStatus.Completed, "care");

        Assert.Equal(0, await _planner.PlanForVisitAsync(v.Id, _patient.Id, v.StartAt, ["care"], default));
        Assert.Equal(1, await _planner.PlanForVisitAsync(v.Id, _patient.Id, v.StartAt, ["care", "botox"], default));
    }

    [Fact]
    public async Task Давно_не_были_планируется_один_раз_и_не_при_будущей_записи()
    {
        _db.CareScenarios.Add(new CareScenario
        {
            Name = "Давно не были", Trigger = ScenarioTrigger.NoVisitFor, NoVisitDays = 180, Steps = { Step(0) },
        });
        _patient.LastVisitAt = _clock.Utc.AddDays(-200);
        _db.SaveChanges();

        Assert.Equal(1, await _planner.RunNoVisitScanAsync(default));
        Assert.Equal(0, await _planner.RunNoVisitScanAsync(default)); // повторный запуск ничего не дублирует

        var other = new Patient { FullName = "Записан Уже", Phone = "+79990000002", LastVisitAt = _clock.Utc.AddDays(-300) };
        _db.Patients.Add(other);
        _db.SaveChanges();
        _db.Visits.Add(new Visit { PatientId = other.Id, StartAt = _clock.Utc.AddDays(3), Status = VisitStatus.Planned, DoctorName = "" });
        _db.SaveChanges();
        Assert.Equal(0, await _planner.RunNoVisitScanAsync(default));
    }

    // ── Отправка ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Наступившее_напоминание_превращается_в_заявку_на_отправку()
    {
        var r = AddDue();

        Assert.Equal(1, await _dispatcher.RunAsync(default));

        var sent = _db.PlannedReminders.AsNoTracking().Single(x => x.Id == r.Id);
        Assert.Equal(ReminderStatus.Sent, sent.Status);
        Assert.NotNull(sent.RequestId);
        var request = _db.SmsRequests.Include(x => x.Recipients).Single(x => x.Id == sent.RequestId);
        Assert.Equal(RequestStatus.Approved, request.Status);
        Assert.Equal("+79990000001", Assert.Single(request.Recipients).Phone);
    }

    [Fact]
    public async Task В_окно_тишины_ничего_не_уходит_а_утром_уходит()
    {
        _clock.SetMoscow(2026, 10, 20, 22, 30);
        var r = AddDue();

        Assert.Equal(0, await _dispatcher.RunAsync(default));
        Assert.Equal(ReminderStatus.Planned, Reminders().Single().Status);

        _clock.SetMoscow(2026, 10, 21, 8, 59);
        Assert.Equal(0, await _dispatcher.RunAsync(default));

        _clock.SetMoscow(2026, 10, 21, 9, 0);
        Assert.Equal(1, await _dispatcher.RunAsync(default));
        Assert.Equal(ReminderStatus.Sent, _db.PlannedReminders.AsNoTracking().Single(x => x.Id == r.Id).Status);
    }

    [Fact]
    public async Task Не_больше_одного_напоминания_в_день_остальные_на_завтра()
    {
        var first = AddDue();
        var second = AddDue();

        Assert.Equal(1, await _dispatcher.RunAsync(default));

        var moved = _db.PlannedReminders.AsNoTracking().Single(x => x.Id == second.Id);
        Assert.Equal(ReminderStatus.Sent, _db.PlannedReminders.AsNoTracking().Single(x => x.Id == first.Id).Status);
        Assert.Equal(ReminderStatus.Planned, moved.Status);
        Assert.Equal(new DateOnly(2026, 10, 21), MoscowTime.Date(moved.DueAt));
        Assert.Contains("одного напоминания в день", moved.StatusReason);

        // Повторный запуск в тот же день ничего не шлёт: перенесённое ещё не наступило.
        Assert.Equal(0, await _dispatcher.RunAsync(default));
    }

    [Fact]
    public async Task Рекламное_не_отправляется_без_модуля_согласий()
    {
        AddDue(ScenarioPurpose.Marketing);

        Assert.Equal(0, await _dispatcher.RunAsync(default));

        var r = Reminders().Single();
        Assert.Equal(ReminderStatus.Skipped, r.Status);
        Assert.Contains("согласий", r.StatusReason);
        Assert.Empty(_db.SmsRequests);
    }

    [Fact]
    public async Task Отписанному_не_отправляется()
    {
        _patient.IsSubscribed = false;
        _db.SaveChanges();
        AddDue();

        Assert.Equal(0, await _dispatcher.RunAsync(default));
        Assert.Equal(ReminderStatus.Skipped, Reminders().Single().Status);
    }

    [Fact]
    public async Task Отмена_если_есть_будущая_запись()
    {
        var s = AddScenario(steps: [Step(14, cancelIfFuture: true)]);
        AddDue(step: s.Steps[0]);
        AddVisit(_clock.Utc.AddDays(2), VisitStatus.Planned);

        Assert.Equal(0, await _dispatcher.RunAsync(default));
        var r = Reminders().Single();
        Assert.Equal(ReminderStatus.Cancelled, r.Status);
        Assert.Contains("будущая запись", r.StatusReason);
    }

    [Fact]
    public async Task Отмена_если_после_события_уже_была_эта_услуга()
    {
        var s = AddScenario(steps: [Step(120, cancelIfService: "botox")]);
        var eventAt = _clock.Utc.AddDays(-30);
        AddDue(step: s.Steps[0], eventAt: eventAt);
        AddVisit(eventAt.AddDays(10), VisitStatus.Completed, "botox");

        Assert.Equal(0, await _dispatcher.RunAsync(default));
        Assert.Equal(ReminderStatus.Cancelled, Reminders().Single().Status);
    }

    [Fact]
    public async Task Другая_услуга_после_события_не_отменяет()
    {
        var s = AddScenario(steps: [Step(120, cancelIfService: "botox")]);
        var eventAt = _clock.Utc.AddDays(-30);
        AddDue(step: s.Steps[0], eventAt: eventAt);
        AddVisit(eventAt.AddDays(10), VisitStatus.Completed, "care");

        Assert.Equal(1, await _dispatcher.RunAsync(default));
    }

    [Fact]
    public async Task Выключенный_сценарий_отменяет_свои_напоминания()
    {
        var s = AddScenario();
        var v = AddVisit(_clock.Utc.AddDays(-20), VisitStatus.Completed, "botox");
        await _planner.PlanForVisitAsync(v.Id, _patient.Id, v.StartAt, ["botox"], default);
        s.IsActive = false;
        _db.SaveChanges();

        Assert.Equal(0, await _dispatcher.RunAsync(default));
        Assert.Equal(ReminderStatus.Cancelled, Reminders().Single().Status);
    }
}
