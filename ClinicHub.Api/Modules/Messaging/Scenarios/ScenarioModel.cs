using ClinicHub.Api.Modules.Catalog;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Messaging.Scenarios;

/// <summary>
/// Схема БД сценариев. Переносимо (ADR-0009). Внешних ключей от напоминаний к шагам и сценариям «без действия»:
/// иначе SQL Server ругается на несколько каскадных путей; при удалении сценария связи чистит код.
/// </summary>
public static class ScenarioModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CareScenario>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(200).IsRequired();
            e.Property(s => s.Purpose).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.Trigger).HasConversion<string>().HasMaxLength(16);
        });

        modelBuilder.Entity<CareScenarioService>(e =>
        {
            e.HasKey(x => new { x.ScenarioId, x.ServiceId });
            e.Property(x => x.ServiceId).HasMaxLength(64);
            e.HasOne(x => x.Scenario).WithMany(s => s.TriggerServices).HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Service>().WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CareScenarioStep>(e =>
        {
            e.Property(s => s.Text).HasMaxLength(1000).IsRequired();
            e.Property(s => s.CancelIfServiceAfterId).HasMaxLength(64);
            e.HasOne(s => s.Scenario).WithMany(c => c.Steps).HasForeignKey(s => s.ScenarioId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlannedReminder>(e =>
        {
            e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Purpose).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Text).HasMaxLength(1000).IsRequired();
            e.Property(r => r.StatusReason).HasMaxLength(300);

            e.HasOne(r => r.Patient).WithMany().HasForeignKey(r => r.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(r => r.Scenario).WithMany().HasForeignKey(r => r.ScenarioId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne(r => r.Step).WithMany().HasForeignKey(r => r.StepId).OnDelete(DeleteBehavior.NoAction);

            e.HasIndex(r => new { r.Status, r.DueAt });
            e.HasIndex(r => new { r.PatientId, r.Status });
            e.HasIndex(r => new { r.ScenarioId, r.PatientId });
        });
    }
}
