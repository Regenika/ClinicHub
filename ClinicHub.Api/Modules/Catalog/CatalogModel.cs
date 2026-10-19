using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Catalog;

/// <summary>Схема БД модуля Catalog. Только переносимые настройки (ADR-0009): явные длины, без SQL-функций.</summary>
public static class CatalogModel
{
    public const string Schema = "catalog";

    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ServiceCategory>(e =>
        {
            e.ToTable("ServiceCategories", Schema);
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Service>(e =>
        {
            e.ToTable("Services", Schema);
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasMaxLength(64);
            e.Property(s => s.Name).HasMaxLength(200).IsRequired();
            e.Property(s => s.Description).HasMaxLength(2000).IsRequired();
            e.Property(s => s.PriceFrom).HasPrecision(10, 2);
            e.Property(s => s.PriceTo).HasPrecision(10, 2);
            e.Property(s => s.PriceDisplay).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.PriceUnit).HasMaxLength(30);
            e.Property(s => s.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.ExternalId).HasMaxLength(100);
            e.HasOne(s => s.Category).WithMany(c => c.Services).HasForeignKey(s => s.CategoryId).OnDelete(DeleteBehavior.SetNull);
            // Уникальность внешнего id в пределах источника. На nullable-колонке EF для SQL Server сам добавляет
            // фильтр IS NOT NULL, а PostgreSQL и так допускает несколько NULL — переносимо.
            e.HasIndex(s => new { s.Source, s.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<Doctor>(e =>
        {
            e.ToTable("Doctors", Schema);
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasMaxLength(64);
            e.Property(d => d.Name).HasMaxLength(200).IsRequired();
            e.Property(d => d.Role).HasMaxLength(300).IsRequired();
            e.Property(d => d.Description).HasMaxLength(2000).IsRequired();
            e.Property(d => d.Source).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.ExternalId).HasMaxLength(100);
            e.HasIndex(d => new { d.Source, d.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<DoctorService>(e =>
        {
            e.ToTable("DoctorServices", Schema);
            e.HasKey(x => new { x.DoctorId, x.ServiceId });
            e.HasOne(x => x.Doctor).WithMany(d => d.Services).HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Service).WithMany(s => s.Doctors).HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PriceHistory>(e =>
        {
            e.ToTable("PriceHistory", Schema);
            e.Property(p => p.ServiceId).HasMaxLength(64).IsRequired();
            e.Property(p => p.OldPriceFrom).HasPrecision(10, 2);
            e.Property(p => p.NewPriceFrom).HasPrecision(10, 2);
            e.Property(p => p.OldPriceTo).HasPrecision(10, 2);
            e.Property(p => p.NewPriceTo).HasPrecision(10, 2);
            e.Property(p => p.OldDisplay).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.NewDisplay).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.ChangedBy).HasMaxLength(100).IsRequired();
            // Без внешнего ключа: история должна пережить удаление услуги.
            e.HasIndex(p => new { p.ServiceId, p.ChangedAt });
        });
    }
}
