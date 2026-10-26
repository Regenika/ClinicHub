using System.Text.RegularExpressions;
using ClinicHub.Web.Blocks;
using ClinicHub.Web.Data;
using ClinicHub.Web.Site;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Web.Admin;

public sealed record PageRow(int Id, string Path, string Title, bool IsPublished, bool HasPublished, bool HasDraft, DateTime? PublishedAt, string? PublishedBy);
public sealed record VersionRow(int Id, PageVersionStatus Status, DateTime CreatedAt, DateTime? PublishedAt, string? PublishedBy, int BlockCount);
public sealed record DraftBlock(int Id, string Type, bool IsVisible, object Settings, string? Error);

/// <summary>Страница в редакторе: свойства, черновик (если начат) и история версий.</summary>
public sealed record PageForEdit(
    int Id, string Path, string Title, string Description, bool IsPublished,
    int? DraftId, IReadOnlyList<DraftBlock> DraftBlocks, IReadOnlyList<VersionRow> Versions);

/// <summary>
/// Редактор страниц из блоков (ADR-0007 §1, PLAN 4.8). Правится только черновик; «Опубликовать» делает его
/// опубликованной версией, а прежняя опубликованная становится архивной — к ней можно вернуться.
/// Сайт читает только опубликованные версии, так что черновик посетители не видят.
/// </summary>
public sealed partial class PageEditor(IDbContextFactory<SiteDbContext> dbFactory, SiteCache siteCache, TimeProvider clock, ILogger<PageEditor> logger)
{
    /// <summary>Адреса страниц с собственным кодом и служебные — страницу из блоков туда не поставить.</summary>
    private static readonly string[] ReservedPrefixes =
        ["/zapis", "/politika", "/pravila", "/admin", "/nastroika-bd", "/not-found", "/error", "/_framework", "/_blazor", "/robots.txt", "/sitemap.xml"];

    [GeneratedRegex("^/([a-z0-9-]+(/[a-z0-9-]+)*)?$")]
    private static partial Regex PathPattern();

    public static string? ValidatePath(string path)
    {
        if (path.Length > 200 || !PathPattern().IsMatch(path))
            return "Адрес: со слеша, латинские строчные буквы, цифры, дефис и «/» между частями, например /uslugi или /akcii/leto.";
        if (ReservedPrefixes.Any(r => path == r || path.StartsWith(r + "/", StringComparison.Ordinal)))
            return "Этот адрес занят служебной страницей сайта.";
        return null;
    }

    public async Task<List<PageRow>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Pages.AsNoTracking().OrderBy(p => p.Path)
            .Select(p => new PageRow(
                p.Id, p.Path, p.Title, p.IsPublished,
                p.Versions.Any(v => v.Status == PageVersionStatus.Published),
                p.Versions.Any(v => v.Status == PageVersionStatus.Draft),
                p.Versions.Where(v => v.Status == PageVersionStatus.Published).Select(v => v.PublishedAt).FirstOrDefault(),
                p.Versions.Where(v => v.Status == PageVersionStatus.Published).Select(v => v.PublishedBy).FirstOrDefault()))
            .ToListAsync();
    }

    public async Task<PageForEdit?> GetAsync(int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId);
        if (page is null) return null;

        var versions = await db.PageVersions.AsNoTracking()
            .Where(v => v.PageId == pageId && v.Status != PageVersionStatus.Draft)
            .OrderByDescending(v => v.PublishedAt).ThenByDescending(v => v.Id)
            .Select(v => new VersionRow(v.Id, v.Status, v.CreatedAt, v.PublishedAt, v.PublishedBy, v.Blocks.Count))
            .ToListAsync();

        var draft = await db.PageVersions.AsNoTracking()
            .Where(v => v.PageId == pageId && v.Status == PageVersionStatus.Draft)
            .Select(v => new { v.Id, Blocks = v.Blocks.OrderBy(b => b.Order).ToList() })
            .FirstOrDefaultAsync();

        var blocks = draft?.Blocks.Select(ToDraftBlock).ToList() ?? [];
        return new PageForEdit(page.Id, page.Path, page.Title, page.Description, page.IsPublished, draft?.Id, blocks, versions);
    }

    private static DraftBlock ToDraftBlock(Block b)
    {
        if (!BlockTypes.All.TryGetValue(b.Type, out var type))
            return new DraftBlock(b.Id, b.Type, b.IsVisible, new object(), $"Неизвестный тип блока «{b.Type}» — на сайте он не показывается. Удалите его.");
        try
        {
            return new DraftBlock(b.Id, b.Type, b.IsVisible, type.Deserialize(b.Data), null);
        }
        catch (System.Text.Json.JsonException)
        {
            return new DraftBlock(b.Id, b.Type, b.IsVisible, Activator.CreateInstance(type.SettingsType)!,
                "Настройки блока повреждены — показаны значения по умолчанию. Сохраните блок, чтобы исправить.");
        }
    }

    /// <summary>Новая страница с пустым черновиком. На сайте появится после первой публикации.</summary>
    public async Task<(int? PageId, string? Error)> CreateAsync(string path, string title, string description)
    {
        path = SiteStore.NormalizePath(path);
        if (ValidatePath(path) is { } error)
            return (null, error);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.Pages.AnyAsync(p => p.Path == path))
            return (null, "Страница с таким адресом уже есть.");
        var now = clock.GetUtcNow().UtcDateTime;
        var page = new Page
        {
            Path = path,
            Title = title.Trim(),
            Description = description.Trim(),
            IsPublished = false,
            CreatedAt = now,
            Versions = [new PageVersion { Status = PageVersionStatus.Draft, CreatedAt = now }],
        };
        db.Pages.Add(page);
        await db.SaveChangesAsync();
        return (page.Id, null);
    }

    /// <summary>Заголовок и описание для поисковиков. Меняются сразу, без публикации (как и в ADR-0007 §1 — это свойства страницы).</summary>
    public async Task SaveSeoAsync(int pageId, string title, string description)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.Pages.FirstAsync(p => p.Id == pageId);
        page.Title = title.Trim();
        page.Description = description.Trim();
        await db.SaveChangesAsync();
        siteCache.Clear();
    }

    /// <summary>Черновик страницы; если его нет — копия опубликованной (или последней) версии.</summary>
    public async Task<int> EnsureDraftAsync(int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var existing = await db.PageVersions.Where(v => v.PageId == pageId && v.Status == PageVersionStatus.Draft).Select(v => (int?)v.Id).FirstOrDefaultAsync();
        if (existing is { } id) return id;

        var source = await db.PageVersions.Include(v => v.Blocks)
            .Where(v => v.PageId == pageId)
            .OrderByDescending(v => v.Status == PageVersionStatus.Published).ThenByDescending(v => v.PublishedAt).ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync();
        var draft = NewDraft(pageId, source?.Blocks ?? []);
        db.PageVersions.Add(draft);
        await db.SaveChangesAsync();
        return draft.Id;
    }

    /// <summary>Заменить черновик копией версии из истории (откат). Публикуется отдельно, после проверки.</summary>
    public async Task RestoreToDraftAsync(int pageId, int versionId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var source = await db.PageVersions.Include(v => v.Blocks).FirstAsync(v => v.Id == versionId && v.PageId == pageId);
        await db.PageVersions.Where(v => v.PageId == pageId && v.Status == PageVersionStatus.Draft).ExecuteDeleteAsync();
        db.PageVersions.Add(NewDraft(pageId, source.Blocks));
        await db.SaveChangesAsync();
    }

    public async Task DiscardDraftAsync(int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.PageVersions.Where(v => v.PageId == pageId && v.Status == PageVersionStatus.Draft).ExecuteDeleteAsync();
    }

    private PageVersion NewDraft(int pageId, IEnumerable<Block> blocks) => new()
    {
        PageId = pageId,
        Status = PageVersionStatus.Draft,
        CreatedAt = clock.GetUtcNow().UtcDateTime,
        Blocks = blocks.OrderBy(b => b.Order).Select((b, i) => new Block
        {
            Type = b.Type,
            Order = (i + 1) * 10,
            IsVisible = b.IsVisible,
            Data = b.Data,
        }).ToList(),
    };

    // ── Блоки черновика ─────────────────────────────────────────────────────

    public async Task AddBlockAsync(int draftId, string type)
    {
        var blockType = BlockTypes.All[type];
        await using var db = await dbFactory.CreateDbContextAsync();
        var max = await db.Blocks.Where(b => b.PageVersionId == draftId).MaxAsync(b => (int?)b.Order) ?? 0;
        db.Blocks.Add(new Block
        {
            PageVersionId = await DraftIdOrThrow(db, draftId),
            Type = type,
            Order = max + 10,
            Data = BlockTypes.Serialize(Activator.CreateInstance(blockType.SettingsType)!),
        });
        await db.SaveChangesAsync();
    }

    public async Task SaveBlockAsync(int blockId, object settings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var block = await DraftBlockOrThrow(db, blockId);
        block.Data = BlockTypes.Serialize(settings);
        await db.SaveChangesAsync();
    }

    public async Task SetBlockVisibleAsync(int blockId, bool visible)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var block = await DraftBlockOrThrow(db, blockId);
        block.IsVisible = visible;
        await db.SaveChangesAsync();
    }

    public async Task MoveBlockAsync(int blockId, int direction)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var block = await DraftBlockOrThrow(db, blockId);
        var list = await db.Blocks.Where(b => b.PageVersionId == block.PageVersionId).OrderBy(b => b.Order).ThenBy(b => b.Id).ToListAsync();
        var i = list.FindIndex(b => b.Id == blockId);
        var j = i + direction;
        if (j >= 0 && j < list.Count)
            (list[i], list[j]) = (list[j], list[i]);
        for (var k = 0; k < list.Count; k++)
            list[k].Order = (k + 1) * 10;
        await db.SaveChangesAsync();
    }

    public async Task DeleteBlockAsync(int blockId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var block = await DraftBlockOrThrow(db, blockId);
        db.Blocks.Remove(block);
        await db.SaveChangesAsync();
    }

    /// <summary>Править можно только блоки черновика: опубликованные и архивные версии неизменны.</summary>
    private static async Task<Block> DraftBlockOrThrow(SiteDbContext db, int blockId) =>
        await db.Blocks.FirstOrDefaultAsync(b => b.Id == blockId && b.PageVersion.Status == PageVersionStatus.Draft)
        ?? throw new InvalidOperationException("Блок не найден в черновике — возможно, черновик уже опубликовали или удалили. Обновите страницу.");

    private static async Task<int> DraftIdOrThrow(SiteDbContext db, int draftId) =>
        await db.PageVersions.AnyAsync(v => v.Id == draftId && v.Status == PageVersionStatus.Draft)
            ? draftId
            : throw new InvalidOperationException("Черновик не найден — возможно, его уже опубликовали. Обновите страницу.");

    // ── Публикация ──────────────────────────────────────────────────────────

    /// <summary>Черновик → опубликованная версия, прежняя опубликованная → архив. Страница становится видна на сайте.</summary>
    public async Task<string?> PublishAsync(int pageId, string editor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var versions = await db.PageVersions.Where(v => v.PageId == pageId && v.Status != PageVersionStatus.Archived).ToListAsync();
        var draft = versions.FirstOrDefault(v => v.Status == PageVersionStatus.Draft);
        if (draft is null)
            return "Черновика нет — публиковать нечего.";

        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var v in versions.Where(v => v.Status == PageVersionStatus.Published))
            v.Status = PageVersionStatus.Archived;
        draft.Status = PageVersionStatus.Published;
        draft.PublishedAt = now;
        draft.PublishedBy = editor;
        var page = await db.Pages.FirstAsync(p => p.Id == pageId);
        page.IsPublished = true;
        await db.SaveChangesAsync();
        siteCache.Clear();
        logger.LogInformation("Страница {Path} опубликована (версия {VersionId}, {Editor})", page.Path, draft.Id, editor);
        return null;
    }

    /// <summary>Снять с публикации (страница отвечает 404) или вернуть. Главную снять нельзя.</summary>
    public async Task<string?> SetPublishedAsync(int pageId, bool published, string editor)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.Pages.FirstAsync(p => p.Id == pageId);
        if (!published && page.Path == "/")
            return "Главную страницу снять с публикации нельзя.";
        if (published && !await db.PageVersions.AnyAsync(v => v.PageId == pageId && v.Status == PageVersionStatus.Published))
            return "У страницы ещё нет опубликованной версии — опубликуйте черновик.";
        page.IsPublished = published;
        await db.SaveChangesAsync();
        siteCache.Clear();
        logger.LogInformation("Страница {Path} {State} ({Editor})", page.Path, published ? "снова на сайте" : "снята с публикации", editor);
        return null;
    }

    /// <summary>Черновик для предпросмотра: блоки отрисовываются теми же компонентами, что и на сайте.</summary>
    public async Task<PublishedPage?> DraftForPreviewAsync(int pageId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pageId);
        if (page is null) return null;
        var version = await db.PageVersions.AsNoTracking().Include(v => v.Blocks)
            .Where(v => v.PageId == pageId)
            .OrderByDescending(v => v.Status == PageVersionStatus.Draft).ThenByDescending(v => v.Status == PageVersionStatus.Published).ThenByDescending(v => v.Id)
            .FirstOrDefaultAsync();
        return new PublishedPage(page.Path, page.Title, page.Description, SiteStore.Render(version?.Blocks ?? [], page.Path, logger));
    }
}
