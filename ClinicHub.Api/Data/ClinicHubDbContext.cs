using ClinicHub.Api.Modules.Catalog;
using ClinicHub.Api.Modules.Messaging.Scenarios;
using ClinicHub.Api.Modules.Visits;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Data;

public class ClinicHubDbContext : DbContext
{
    public ClinicHubDbContext(DbContextOptions<ClinicHubDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<SmsRequest> SmsRequests => Set<SmsRequest>();
    public DbSet<RequestRecipient> RequestRecipients => Set<RequestRecipient>();
    public DbSet<Phone> Phones => Set<Phone>();
    public DbSet<SimCard> SimCards => Set<SimCard>();
    public DbSet<BalanceLog> BalanceLogs => Set<BalanceLog>();
    public DbSet<SmsQueueItem> SmsQueue => Set<SmsQueueItem>();
    public DbSet<SmsLog> SmsLogs => Set<SmsLog>();
    public DbSet<RateConfig> RateConfigs => Set<RateConfig>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<AutoRule> AutoRules => Set<AutoRule>();
    public DbSet<AutoRuleFire> AutoRuleFires => Set<AutoRuleFire>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();

    // Модуль Catalog (схема catalog, ADR-0007 §2, ADR-0008 §2)
    public DbSet<ServiceCategory> ServiceCategories => Set<ServiceCategory>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<Doctor> Doctors => Set<Doctor>();
    public DbSet<DoctorService> DoctorServices => Set<DoctorService>();
    public DbSet<PriceHistory> PriceHistory => Set<PriceHistory>();

    // Модуль Visits (ADR-0008 §1)
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<VisitService> VisitServices => Set<VisitService>();
    public DbSet<ExternalServiceMap> ExternalServiceMaps => Set<ExternalServiceMap>();

    // Сценарии напоминаний (ADR-0008 §3–4)
    public DbSet<CareScenario> CareScenarios => Set<CareScenario>();
    public DbSet<CareScenarioStep> CareScenarioSteps => Set<CareScenarioStep>();
    public DbSet<PlannedReminder> PlannedReminders => Set<PlannedReminder>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Все DateTime (и DateTime?) — моменты времени в UTC (ADR-0009).
        // Календарные даты (дата рождения и т.п.) ниже переопределены на тип date.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Пользователи
        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.Login).HasMaxLength(64).IsRequired();
            e.HasIndex(u => u.Login).IsUnique();
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(16);
        });

        // Пациенты
        modelBuilder.Entity<Patient>(e =>
        {
            e.Property(p => p.FullName).HasMaxLength(200).IsRequired();
            e.Property(p => p.Phone).HasMaxLength(32).IsRequired();
            e.Property(p => p.DateOfBirth).HasColumnType("date").HasConversion<DateOnlyDateTimeConverter>();
            e.HasIndex(p => p.Phone);
        });

        // Заявки
        modelBuilder.Entity<SmsRequest>(e =>
        {
            e.Property(r => r.Title).HasMaxLength(300).IsRequired();
            e.Property(r => r.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.SendMode).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(r => r.Status);
            e.HasOne(r => r.CreatedBy).WithMany().HasForeignKey(r => r.CreatedById).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(r => r.ApprovedBy).WithMany().HasForeignKey(r => r.ApprovedById).OnDelete(DeleteBehavior.NoAction);
        });

        // Получатели заявки
        modelBuilder.Entity<RequestRecipient>(e =>
        {
            e.HasIndex(r => r.Status);
            e.HasOne(r => r.Request).WithMany(r => r.Recipients).HasForeignKey(r => r.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Patient).WithMany().HasForeignKey(r => r.PatientId).OnDelete(DeleteBehavior.SetNull);
        });

        // Телефоны
        modelBuilder.Entity<Phone>(e =>
        {
            e.Property(p => p.PhoneNumber).HasMaxLength(32).IsRequired();
            e.Property(p => p.AgentToken).HasMaxLength(64);
            e.HasIndex(p => p.PhoneNumber).IsUnique();
        });

        // SIM-карты
        modelBuilder.Entity<SimCard>(e =>
        {
            e.Property(s => s.Number).HasMaxLength(32).IsRequired();
            e.HasOne(s => s.Phone).WithMany(p => p.SimCards).HasForeignKey(s => s.PhoneId).OnDelete(DeleteBehavior.Cascade);
        });

        // Журналы остатка
        modelBuilder.Entity<BalanceLog>(e =>
        {
            e.HasOne(b => b.SimCard).WithMany(s => s.BalanceLogs).HasForeignKey(b => b.SimCardId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(b => b.SimCardId);
        });

        // Очередь SMS
        modelBuilder.Entity<SmsQueueItem>(e =>
        {
            e.HasIndex(q => q.Status);
            e.HasOne(q => q.Recipient).WithMany(r => r.QueueItems).HasForeignKey(q => q.RecipientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(q => q.SimCard).WithMany().HasForeignKey(q => q.SimCardId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(q => q.Phone).WithMany().HasForeignKey(q => q.PhoneId).OnDelete(DeleteBehavior.NoAction);
        });

        // Журнал отправок
        modelBuilder.Entity<SmsLog>(e =>
        {
            e.HasOne(l => l.QueueItem).WithMany().HasForeignKey(l => l.QueueId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(l => l.SimCard).WithMany().HasForeignKey(l => l.SimCardId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(l => l.Phone).WithMany().HasForeignKey(l => l.PhoneId).OnDelete(DeleteBehavior.NoAction);
            e.HasIndex(l => l.SentAt);
        });

        // Настройки
        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(s => s.Key);
        });

        // Авто-правила (день рождения и т.п.)
        modelBuilder.Entity<AutoRule>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(200).IsRequired();
            e.Property(r => r.Trigger).HasConversion<string>().HasMaxLength(32);
        });

        modelBuilder.Entity<AutoRuleFire>(e =>
        {
            e.HasOne(f => f.AutoRule).WithMany(r => r.Fires).HasForeignKey(f => f.AutoRuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(f => f.Patient).WithMany().HasForeignKey(f => f.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.Property(f => f.FiredOnDate).HasColumnType("date").HasConversion<DateOnlyDateTimeConverter>();
            // Идемпотентность: одно и то же правило не срабатывает дважды за один день для одного пациента.
            e.HasIndex(f => new { f.AutoRuleId, f.PatientId, f.FiredOnDate }).IsUnique();
        });

        // Шаблоны текста SMS
        modelBuilder.Entity<MessageTemplate>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200).IsRequired();
        });

        // Модули: каждый описывает свои таблицы сам.
        CatalogModel.Configure(modelBuilder);
        VisitModel.Configure(modelBuilder);
        ScenarioModel.Configure(modelBuilder);
    }
}