using ClinicHub.Web.Blocks;
using ClinicHub.Web.Site;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Web.Data;

/// <summary>
/// Миграции и начальное наполнение БД сайта. Пустая база заполняется из site-content.json (ADR-0007, «Последствия»),
/// чтобы сайт выглядел так же, как до переноса в БД. Заполненную базу не трогаем: правки делаются в админке.
/// </summary>
public static class SiteSeeder
{
    public const string SeedAuthor = "seed";

    public static async Task MigrateAndSeedAsync(string connectionString, SiteContent content, ILogger logger, CancellationToken ct = default)
    {
        var options = new DbContextOptionsBuilder<SiteDbContext>();
        SiteDbContext.Configure(options, connectionString);
        await using var db = new SiteDbContext(options.Options);

        await db.Database.MigrateAsync(ct);

        var now = DateTime.UtcNow;
        if (!await db.Settings.AnyAsync(ct))
        {
            db.Settings.Add(ToSettings(content.Clinic, now));
            logger.LogInformation("Настройки сайта перенесены из site-content.json в БД");
        }
        if (!await db.Pages.AnyAsync(ct))
        {
            db.Pages.Add(HomePage(content, now));
            logger.LogInformation("Главная страница создана в БД из site-content.json");
        }
        // Каталог проверяем отдельно: в базах, созданных до появления каталога в БД, страницы уже есть, а каталога нет.
        if (!await db.CatalogServices.AnyAsync(ct) && !await db.CatalogDoctors.AnyAsync(ct))
        {
            SeedCatalog(db, content, now);
            logger.LogInformation("Каталог услуг и врачей перенесён из site-content.json в БД");
        }
        await db.SaveChangesAsync(ct);
    }

    private static void SeedCatalog(SiteDbContext db, SiteContent content, DateTime now)
    {
        var services = content.Services.Select((s, i) => new CatalogService
        {
            Key = s.Id,
            Name = s.Name,
            Text = s.Text,
            Price = s.Price,
            DurationMinutes = s.DurationMinutes,
            Order = (i + 1) * 10,
            UpdatedAt = now,
        }).ToList();
        db.CatalogServices.AddRange(services);

        db.CatalogDoctors.AddRange(content.Doctors.Select((d, i) => new CatalogDoctor
        {
            Key = d.Id,
            Name = d.Name,
            Role = d.Role,
            Order = (i + 1) * 10,
            UpdatedAt = now,
            Services = d.ServiceIds
                .Select(id => services.FirstOrDefault(s => s.Key == id))
                .OfType<CatalogService>()
                .Select(s => new CatalogDoctorService { Service = s })
                .ToList(),
        }));
    }

    private static SiteSettings ToSettings(ClinicInfo c, DateTime now) => new()
    {
        Name = c.Name,
        Tagline = c.Tagline,
        LegalName = c.LegalName,
        License = c.License,
        City = c.City,
        Address = c.Address,
        Metro = c.Metro,
        Hours = c.Hours,
        Phone = c.Phone,
        BaseUrl = c.BaseUrl,
        TelegramUrl = c.TelegramUrl,
        MaxUrl = c.MaxUrl,
        VkUrl = c.VkUrl,
        ProDoktorovUrl = c.ProDoktorov?.Url ?? "",
        ProDoktorovRating = c.ProDoktorov?.Rating ?? "",
        YandexMapsUrl = c.YandexMaps?.Url ?? "",
        YandexMapsRating = c.YandexMaps?.Rating ?? "",
        UpdatedAt = now,
    };

    /// <summary>Главная в том виде, в каком она была свёрстана в Home.razor до переноса в БД.</summary>
    private static Page HomePage(SiteContent content, DateTime now)
    {
        var c = content.Clinic;
        object[] blocks =
        [
            new HeroBlock(),
            new QuickBookingBlock(),
            new ServicesBlock(),
            new DoctorsBlock(),
            new ResultsBlock { Items = content.Results },
            new ReviewsBlock { Quote = content.Review },
            new ChannelsBlock(),
            new PostsBlock { Items = content.Posts },
        ];

        return new Page
        {
            Path = "/",
            Title = $"{c.Name} — {c.Tagline.ToLowerInvariant()} в {CityPrepositional(c.City)}",
            Description = $"{c.Tagline} {c.Name}: консультация врача-косметолога, уход за кожей, аппаратные и инъекционные методики. Онлайн-запись, напоминания в мессенджерах и по SMS.",
            IsPublished = true,
            CreatedAt = now,
            Versions =
            [
                new PageVersion
                {
                    Status = PageVersionStatus.Published,
                    CreatedAt = now,
                    PublishedAt = now,
                    PublishedBy = SeedAuthor,
                    Blocks = blocks.Select((settings, i) => new Block
                    {
                        Type = BlockTypes.KeyOf(settings.GetType()),
                        Order = (i + 1) * 10,
                        Data = BlockTypes.Serialize(settings),
                    }).ToList(),
                },
            ],
        };
    }

    private static string CityPrepositional(string city) =>
        city == "Санкт-Петербург" ? "Санкт-Петербурге" : city;
}
