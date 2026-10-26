using ClinicHub.Api.Modules.Catalog;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Visits;

/// <summary>Схема БД модуля Visits (схема по умолчанию, как у остальных таблиц сервера). Переносимо (ADR-0009).</summary>
public static class VisitModel
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Visit>(e =>
        {
            e.Property(v => v.DoctorId).HasMaxLength(64);
            e.Property(v => v.DoctorName).HasMaxLength(200).IsRequired();
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(v => v.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(v => v.ExternalId).HasMaxLength(100);
            e.Property(v => v.Comment).HasMaxLength(1000);

            // Пациента с визитами удалить можно — его визиты уходят вместе с ним (как и прочие его данные).
            e.HasOne(v => v.Patient).WithMany().HasForeignKey(v => v.PatientId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Doctor>().WithMany().HasForeignKey(v => v.DoctorId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(v => v.ConfirmedBy).WithMany().HasForeignKey(v => v.ConfirmedById).OnDelete(DeleteBehavior.NoAction);

            e.HasIndex(v => new { v.PatientId, v.StartAt });
            e.HasIndex(v => v.StartAt);
            // Одна внешняя запись — один визит. На nullable-колонке переносимо (см. CatalogModel).
            e.HasIndex(v => new { v.Source, v.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<VisitService>(e =>
        {
            e.Property(s => s.ServiceId).HasMaxLength(64).IsRequired();
            e.Property(s => s.ServiceName).HasMaxLength(200).IsRequired();
            e.HasOne(s => s.Visit).WithMany(v => v.Services).HasForeignKey(s => s.VisitId).OnDelete(DeleteBehavior.Cascade);
            // Услугу, которая была в визитах, удалить нельзя (API предлагает скрыть её с сайта).
            e.HasOne<Service>().WithMany().HasForeignKey(s => s.ServiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(s => s.ServiceId);
        });

        modelBuilder.Entity<ExternalServiceMap>(e =>
        {
            e.Property(m => m.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.ExternalKey).HasMaxLength(300).IsRequired();
            e.Property(m => m.ServiceId).HasMaxLength(64).IsRequired();
            e.HasOne<Service>().WithMany().HasForeignKey(m => m.ServiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => new { m.Source, m.ExternalKey }).IsUnique();
        });
    }
}
