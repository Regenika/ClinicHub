using ClinicHub.Web.Blocks;
using ClinicHub.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Web.Site;

/// <summary>Опубликованная страница, готовая к отрисовке: блоки уже разобраны из JSON.</summary>
public sealed record PublishedPage(string Path, string Title, string Description, IReadOnlyList<RenderedBlock> Blocks);

public sealed record RenderedBlock(int Id, BlockType Type, object Settings);

/// <summary>
/// Чтение сайта из БД для публичных страниц. Живёт в пределах запроса: настройки читаются один раз,
/// даже если их просят шапка, подвал и SEO одной страницы.
/// Шапка и страница при отрисовке ждут данные одновременно, а один DbContext параллельных запросов не допускает,
/// поэтому у каждого чтения свой контекст из фабрики. Между запросами данные держит <see cref="SiteCache"/>.
/// </summary>
public sealed class SiteStore(IDbContextFactory<SiteDbContext> dbFactory, SiteCache cache, ILogger<SiteStore> logger)
{
    private Task<ClinicInfo>? _clinic;

    public Task<ClinicInfo> GetClinicAsync() => _clinic ??= GetClinicCachedAsync();

    private async Task<ClinicInfo> GetClinicCachedAsync() =>
        await cache.GetAsync<ClinicInfo>("settings", LoadClinicAsync) ?? new ClinicInfo();

    /// <summary>Опубликованная версия страницы или null, если страницы нет или она снята с публикации.</summary>
    public Task<PublishedPage?> GetPublishedAsync(string path)
    {
        path = NormalizePath(path);
        // Без токена отмены запроса: загрузка может продолжиться в фоне и обновить кэш после ответа посетителю.
        return cache.GetAsync("page:" + path, () => LoadPublishedAsync(path, CancellationToken.None));
    }

    /// <summary>Адреса опубликованных страниц для sitemap.xml.</summary>
    public async Task<List<string>> GetPublishedPathsAsync() =>
        await cache.GetAsync<List<string>>("paths", () => LoadPublishedPathsAsync(CancellationToken.None)) ?? [];

    private async Task<ClinicInfo?> LoadClinicAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var s = await db.Settings.AsNoTracking().OrderBy(x => x.Id).FirstOrDefaultAsync();
        return s is null ? new ClinicInfo() : ToClinicInfo(s);
    }

    private async Task<PublishedPage?> LoadPublishedAsync(string path, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var page = await db.Pages.AsNoTracking()
            .Where(p => p.Path == path && p.IsPublished)
            .Select(p => new
            {
                p.Path,
                p.Title,
                p.Description,
                Version = p.Versions
                    .Where(v => v.Status == PageVersionStatus.Published)
                    .OrderByDescending(v => v.PublishedAt)
                    .Select(v => new { v.Id, Blocks = v.Blocks.Where(b => b.IsVisible).OrderBy(b => b.Order).ToList() })
                    .FirstOrDefault(),
            })
            .FirstOrDefaultAsync(ct);
        if (page?.Version is null) return null;
        return new PublishedPage(page.Path, page.Title, page.Description, Render(page.Version.Blocks, page.Path, logger));
    }

    /// <summary>Видимые блоки версии, готовые к отрисовке. Общий для сайта и предпросмотра черновика в админке.</summary>
    public static List<RenderedBlock> Render(IEnumerable<Block> source, string path, ILogger logger)
    {
        var blocks = new List<RenderedBlock>();
        foreach (var b in source.Where(b => b.IsVisible).OrderBy(b => b.Order))
        {
            // Неизвестный тип или битый JSON — пропускаем блок и пишем в журнал, а страницу показываем.
            if (!BlockTypes.All.TryGetValue(b.Type, out var type))
            {
                logger.LogWarning("Блок {BlockId} страницы {Path}: неизвестный тип «{Type}», пропущен", b.Id, path, b.Type);
                continue;
            }
            try
            {
                blocks.Add(new RenderedBlock(b.Id, type, type.Deserialize(b.Data)));
            }
            catch (System.Text.Json.JsonException ex)
            {
                logger.LogWarning(ex, "Блок {BlockId} страницы {Path}: не удалось прочитать настройки, пропущен", b.Id, path);
            }
        }
        return blocks;
    }

    private async Task<List<string>?> LoadPublishedPathsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Pages.AsNoTracking()
            .Where(p => p.IsPublished && p.Versions.Any(v => v.Status == PageVersionStatus.Published))
            .OrderBy(p => p.Path)
            .Select(p => p.Path)
            .ToListAsync(ct);
    }

    /// <summary>«/Uslugi/» → «/uslugi», пусто → «/».</summary>
    public static string NormalizePath(string? path) => "/" + (path ?? "").Trim().Trim('/').ToLowerInvariant();

    private static ClinicInfo ToClinicInfo(SiteSettings s) => new()
    {
        Name = s.Name,
        Tagline = s.Tagline,
        LegalName = s.LegalName,
        License = s.License,
        City = s.City,
        Address = s.Address,
        Metro = s.Metro,
        Hours = s.Hours,
        Phone = s.Phone,
        BaseUrl = s.BaseUrl,
        TelegramUrl = s.TelegramUrl,
        MaxUrl = s.MaxUrl,
        VkUrl = s.VkUrl,
        ProDoktorov = string.IsNullOrEmpty(s.ProDoktorovUrl) ? null : new RatingLink { Url = s.ProDoktorovUrl, Rating = s.ProDoktorovRating },
        YandexMaps = string.IsNullOrEmpty(s.YandexMapsUrl) ? null : new RatingLink { Url = s.YandexMapsUrl, Rating = s.YandexMapsRating },
    };
}
