using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ClinicHub.Web.Data;

/// <summary>
/// БД сайта: схема <c>site</c> в общей базе ClinicHub (ADR-0007 §4). Своя история миграций
/// <c>site.__EFMigrationsHistory</c>, поэтому миграции сайта не пересекаются с миграциями сервера.
/// Пишем переносимо (ADR-0009): только LINQ, JSON — в текстовой колонке, длины строк заданы явно.
/// </summary>
public sealed class SiteDbContext(DbContextOptions<SiteDbContext> options) : DbContext(options)
{
    public const string Schema = "site";

    public DbSet<Page> Pages => Set<Page>();
    public DbSet<PageVersion> PageVersions => Set<PageVersion>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<SiteSettings> Settings => Set<SiteSettings>();

    // Временный каталог (CatalogEntities.cs).
    public DbSet<CatalogService> CatalogServices => Set<CatalogService>();
    public DbSet<CatalogDoctor> CatalogDoctors => Set<CatalogDoctor>();
    public DbSet<CatalogDoctorService> CatalogDoctorServices => Set<CatalogDoctorService>();

    /// <summary>Общие параметры подключения: и для сайта, и для dotnet ef.</summary>
    public static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options.UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", Schema));

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Все DateTime — моменты времени в UTC (CLAUDE.md, ADR-0009).
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Page>(e =>
        {
            e.ToTable("Pages");
            e.Property(x => x.Path).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(500);
            e.HasIndex(x => x.Path).IsUnique();
        });

        b.Entity<PageVersion>(e =>
        {
            e.ToTable("PageVersions");
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PublishedBy).HasMaxLength(100);
            e.HasOne(x => x.Page).WithMany(x => x.Versions).HasForeignKey(x => x.PageId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.PageId, x.Status });
        });

        b.Entity<Block>(e =>
        {
            e.ToTable("Blocks");
            e.Property(x => x.Type).HasMaxLength(50);
            // Без HasMaxLength: настройки блока могут быть длинными (списки, тексты). Это text в PostgreSQL и nvarchar(max) в SQL Server.
            e.Property(x => x.Data);
            e.HasOne(x => x.PageVersion).WithMany(x => x.Blocks).HasForeignKey(x => x.PageVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.PageVersionId, x.Order });
        });

        b.Entity<SiteSettings>(e =>
        {
            e.ToTable("Settings");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Tagline).HasMaxLength(200);
            e.Property(x => x.LegalName).HasMaxLength(200);
            e.Property(x => x.License).HasMaxLength(200);
            e.Property(x => x.City).HasMaxLength(100);
            e.Property(x => x.Address).HasMaxLength(300);
            e.Property(x => x.Metro).HasMaxLength(100);
            e.Property(x => x.Hours).HasMaxLength(200);
            e.Property(x => x.Phone).HasMaxLength(50);
            e.Property(x => x.BaseUrl).HasMaxLength(200);
            e.Property(x => x.TelegramUrl).HasMaxLength(500);
            e.Property(x => x.MaxUrl).HasMaxLength(500);
            e.Property(x => x.VkUrl).HasMaxLength(500);
            e.Property(x => x.ProDoktorovUrl).HasMaxLength(500);
            e.Property(x => x.ProDoktorovRating).HasMaxLength(20);
            e.Property(x => x.YandexMapsUrl).HasMaxLength(500);
            e.Property(x => x.YandexMapsRating).HasMaxLength(20);
        });

        b.Entity<CatalogService>(e =>
        {
            e.ToTable("CatalogServices");
            e.Property(x => x.Key).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Text).HasMaxLength(1000);
            e.Property(x => x.Price).HasMaxLength(100);
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<CatalogDoctor>(e =>
        {
            e.ToTable("CatalogDoctors");
            e.Property(x => x.Key).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Role).HasMaxLength(300);
            e.HasIndex(x => x.Key).IsUnique();
        });

        b.Entity<CatalogDoctorService>(e =>
        {
            e.ToTable("CatalogDoctorServices");
            e.HasKey(x => new { x.DoctorId, x.ServiceId });
            e.HasOne(x => x.Doctor).WithMany(x => x.Services).HasForeignKey(x => x.DoctorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Service).WithMany().HasForeignKey(x => x.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

/// <summary>
/// Момент времени в БД храним в UTC. Local переводим в UTC, Unspecified считаем уже UTC; при чтении помечаем Kind = Utc.
/// Тот же конвертер, что у сервера (ClinicHub.Api/Data/DateTimeConverters.cs, ветка feature/api-postgres).
/// Когда контексты переедут в один хост (ADR-0007 §4), оставить один.
/// </summary>
public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => ToUtc(v),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
{
    private static DateTime ToUtc(DateTime v) => v.Kind switch
    {
        DateTimeKind.Utc => v,
        DateTimeKind.Local => v.ToUniversalTime(),
        _ => DateTime.SpecifyKind(v, DateTimeKind.Utc),
    };
}

/// <summary>Для dotnet ef: миграции создаются без запуска сайта и без настоящей строки подключения.</summary>
public sealed class SiteDbContextDesignFactory : IDesignTimeDbContextFactory<SiteDbContext>
{
    public SiteDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SiteDbContext>();
        SiteDbContext.Configure(options, "Server=.;Database=ClinicHub;Trusted_Connection=True;TrustServerCertificate=True");
        return new SiteDbContext(options.Options);
    }
}
