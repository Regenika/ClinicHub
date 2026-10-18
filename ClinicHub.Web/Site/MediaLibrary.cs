using System.Security.Cryptography;
using ClinicHub.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ClinicHub.Web.Site;

/// <summary>Сведения о картинке без самих данных — для списков, выбора в формах и alt на сайте.</summary>
public sealed record MediaInfo(int Id, string FileName, string ContentType, int Size, string Alt, DateTime UploadedAt, string UploadedBy)
{
    public string Url => $"/media/{Id}";
}

/// <summary>
/// Медиатека: картинки в БД (MediaFile). Список сведений держится в памяти и обновляется раз в 30 секунд (как каталог);
/// данные картинок — в кэше памяти до 256 МБ, браузеру отдаются с годовым кэшем (у картинки свой id, содержимое не меняется).
/// </summary>
public sealed class MediaLibrary(IDbContextFactory<SiteDbContext> dbFactory, SiteCache siteCache, TimeProvider clock, ILogger<MediaLibrary> logger)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private const long CacheBytes = 256L * 1024 * 1024;

    private readonly MemoryCache Bytes = new(new MemoryCacheOptions { SizeLimit = CacheBytes });

    /// <summary>Сведения обо всех картинках (кэш 30 с, сбрасывается при изменениях из админки).</summary>
    public async Task<IReadOnlyList<MediaInfo>> ListAsync()
    {
        var list = await siteCache.GetAsync<List<MediaInfo>>("media", async () =>
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            return await db.MediaFiles.AsNoTracking().OrderByDescending(m => m.UploadedAt)
                .Select(m => new MediaInfo(m.Id, m.FileName, m.ContentType, m.Size, m.Alt, m.UploadedAt, m.UploadedBy))
                .ToListAsync();
        });
        return list ?? [];
    }

    public async Task<MediaInfo?> InfoAsync(int? id) =>
        id is null ? null : (await ListAsync()).FirstOrDefault(m => m.Id == id);

    /// <summary>Данные картинки для /media/{id}: из памяти или из базы.</summary>
    public async Task<(byte[] Data, string ContentType, string ETag)?> GetAsync(int id)
    {
        if (Bytes.TryGetValue(id, out (byte[], string, string) hit))
            return hit;
        await using var db = await dbFactory.CreateDbContextAsync();
        var file = await db.MediaFiles.AsNoTracking().Where(m => m.Id == id)
            .Select(m => new { m.Data, m.ContentType, m.Sha256 }).FirstOrDefaultAsync();
        if (file is null) return null;
        var value = (file.Data, file.ContentType, file.Sha256);
        Bytes.Set(id, value, new MemoryCacheEntryOptions { Size = Math.Max(1, file.Data.Length) });
        return value;
    }

    /// <summary>Загрузить картинку. Тип определяется по первым байтам файла: JPEG, PNG, WebP, GIF. SVG не принимаем — в нём может быть скрипт.</summary>
    public async Task<(MediaInfo? Info, string? Error)> UploadAsync(string fileName, byte[] data, string uploadedBy)
    {
        if (data.Length == 0)
            return (null, "Файл пустой.");
        if (data.Length > MaxBytes)
            return (null, $"Файл больше {MaxBytes / 1024 / 1024} МБ. Уменьшите картинку (для сайта хватает 2000 пикселей по длинной стороне).");
        var contentType = Sniff(data);
        if (contentType is null)
            return (null, "Это не картинка JPEG, PNG, WebP или GIF.");

        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        await using var db = await dbFactory.CreateDbContextAsync();
        var same = await db.MediaFiles.Where(m => m.Sha256 == sha).Select(m => (int?)m.Id).FirstOrDefaultAsync();
        if (same is { } existingId)
            return (await InfoAsync(existingId), "Эта картинка уже есть в медиатеке — новую копию не создаю.");

        var name = Path.GetFileName(fileName);
        if (name.Length > 200) name = name[^200..];
        var file = new MediaFile
        {
            FileName = name,
            ContentType = contentType,
            Size = data.Length,
            Sha256 = sha,
            Alt = "",
            Data = data,
            UploadedBy = uploadedBy,
            UploadedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.MediaFiles.Add(file);
        await db.SaveChangesAsync();
        siteCache.Clear();
        logger.LogInformation("Загружена картинка {Id} «{Name}», {Size} байт ({By})", file.Id, name, data.Length, uploadedBy);
        return (new MediaInfo(file.Id, file.FileName, file.ContentType, file.Size, file.Alt, file.UploadedAt, file.UploadedBy), null);
    }

    public async Task SetAltAsync(int id, string alt)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var file = await db.MediaFiles.FirstAsync(m => m.Id == id);
        file.Alt = alt.Trim().Length > 300 ? alt.Trim()[..300] : alt.Trim();
        await db.SaveChangesAsync();
        siteCache.Clear();
    }

    /// <summary>Удалить картинку. Там, где она стояла, на сайте снова будет место для фото.</summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.MediaFiles.Where(m => m.Id == id).ExecuteDeleteAsync();
        Bytes.Remove(id);
        siteCache.Clear();
    }

    private static string? Sniff(byte[] d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A) return "image/png";
        if (d.Length >= 6 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F' && d[3] == '8' && (d[4] == '7' || d[4] == '9') && d[5] == 'a') return "image/gif";
        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "image/webp";
        return null;
    }
}

public static class MediaEndpoints
{
    /// <summary>
    /// /media/{id}: картинка из медиатеки. Год в кэше браузера (immutable): у новой картинки всегда новый id.
    /// nosniff — браузер не будет гадать тип по содержимому; тип мы сами определили при загрузке.
    /// </summary>
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/media/{id:int}/{**name}", async (int id, HttpContext context, MediaLibrary media) =>
        {
            if (await media.GetAsync(id) is not { } file)
                return Results.NotFound();
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Bytes(file.Data, file.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{file.ETag}\""));
        });
        return app;
    }
}
