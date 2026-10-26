using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Consents;

/// <summary>Схема consents. Переносимо (ADR-0009): строковые enum, явные длины, без сырого SQL.</summary>
public static class ConsentModel
{
    public const string Schema = "consents";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConsentRecord>(e =>
        {
            e.ToTable("Records", Schema);
            e.Property(c => c.Purpose).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Channel).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.EvidenceRef).HasMaxLength(100);
            e.Property(c => c.Comment).HasMaxLength(500);

            // Удаление пациента (его персональные данные) уносит и его журнал; по одной записи удалять нельзя.
            e.HasOne(c => c.Patient).WithMany().HasForeignKey(c => c.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.By).WithMany().HasForeignKey(c => c.ById).OnDelete(DeleteBehavior.NoAction);

            // Текущее состояние ищется по пациенту и цели, последняя запись — по времени.
            e.HasIndex(c => new { c.PatientId, c.Purpose, c.At });
        });
    }
}
