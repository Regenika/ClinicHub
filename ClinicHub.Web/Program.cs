using System.Text.Encodings.Web;
using System.Text.Unicode;
using ClinicHub.Web.Admin;
using ClinicHub.Web.Booking;
using ClinicHub.Web.Components;
using ClinicHub.Web.Data;
using ClinicHub.Web.Site;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;

var builder = WebApplication.CreateBuilder(args);

// На компьютере клиники сайт работает службой Windows «ClinicHub Web» (deploy/web/install.ps1).
// Предупреждения и ошибки идут в журнал событий «Приложение», источник ClinicHub.Web (его регистрирует установщик).
builder.Services.AddWindowsService(o => o.ServiceName = "ClinicHub Web");
#pragma warning disable CA1416 // Журнал событий есть только в Windows; на других ОС эти настройки просто не читаются.
builder.Services.Configure<EventLogSettings>(o => o.SourceName = "ClinicHub.Web");
#pragma warning restore CA1416

// Журнал в файлы: у службы по умолчанию logs\ рядом с сайтом, при разработке — только если задан Logging:File:Directory.
var logDirectory = builder.Configuration["Logging:File:Directory"]
    ?? (WindowsServiceHelpers.IsWindowsService() ? "logs" : null);
if (logDirectory is not null)
{
    var provider = new FileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, logDirectory), TimeProvider.System);
    builder.Logging.AddProvider(provider);
}

// Ключи шифрования (антифорджери, сохранённое подключение к базе) — в config\keys рядом с сайтом,
// под защитой DPAPI учётной записи сайта. Без явной папки служба держала бы их в своём профиле Windows.
var configDirectory = DbConnectionStore.ConfigDirectory(builder.Configuration, builder.Environment);
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName("ClinicHub.Web")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(configDirectory, "keys")));
if (OperatingSystem.IsWindows())
    dataProtection.ProtectKeysWithDpapi();

// БД сайта: страницы из блоков и настройки, схема site в общей базе ClinicHub (ADR-0007).
// Подключение — из user-secrets / переменной окружения ConnectionStrings__ClinicHub (разработка)
// или со страницы /nastroika-bd (компьютер клиники). Пока его нет, сайт показывает только эту страницу.
builder.Services.AddSingleton<DbConnectionStore>();
builder.Services.AddSingleton<IDbContextFactory<SiteDbContext>, SiteDbContextFactory>();
builder.Services.AddHostedService<DbConnectionMonitor>();
builder.Services.AddSingleton<SiteCache>();
builder.Services.AddScoped<SiteStore>();

// site-content.json — только начальное наполнение пустой БД: страницы, настройки и каталог. Дальше всё правится в БД.
builder.Configuration.AddJsonFile("site-content.json", optional: false, reloadOnChange: false);
// Каталог услуг и врачей — временно в схеме site, пока его нет в ClinicHub.Api (Issue #13).
builder.Services.AddSingleton<DbSiteCatalog>();
builder.Services.AddSingleton<ISiteCatalog>(sp => sp.GetRequiredService<DbSiteCatalog>());

// Веб-админка /admin (ADR-0005, ADR-0007 §5): вход редакторов по куке, только из сети клиники.
builder.Services.AddAuthentication(AdminAuth.Scheme).AddCookie(AdminAuth.Scheme, o =>
{
    o.Cookie.Name = ".ClinicHub.Admin";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    // Пока сайт в сети клиники по HTTP; когда появится HTTPS (PLAN 4.4) — Always.
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.LoginPath = AdminAuth.LoginPath;
    o.AccessDeniedPath = AdminAuth.LoginPath;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    // Отключённый редактор теряет доступ при следующем запросе, а не когда истечёт кука.
    o.Events.OnValidatePrincipal = async ctx =>
    {
        var id = int.TryParse(ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var v) ? v : 0;
        var accounts = ctx.HttpContext.RequestServices.GetRequiredService<EditorAccounts>();
        if (id == 0 || !await accounts.IsActiveAsync(id))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(AdminAuth.Scheme);
        }
    };
});
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<EditorAccounts>();
builder.Services.AddSingleton<CatalogAdmin>();
builder.Services.AddSingleton<PageEditor>();
builder.Services.AddSingleton<MediaLibrary>();

// Кириллицу в HTML отдаём как есть, а не &#x...; — страницы короче и читаемее. Опасные символы (<, >, &, ") по-прежнему экранируются.
builder.Services.AddWebEncoders(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddSingleton(TimeProvider.System);
// Пока МИС не выбрана — демо-слоты. Потом здесь будет адаптер МИС (PLAN, этап 2).
builder.Services.AddSingleton<IBookingSource, DemoBookingSource>();

// Публичная часть — статический SSR без интерактивности: быстро и индексируется (ADR-0003).
// Интерактивный режим (SignalR) — только у страниц админки, им он нужен для форм без перезагрузки.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// Админка и канал интерактивных компонентов (/_blazor) — только из сети клиники. Остальным — как будто их нет.
// Admin:AllowExternal = true открывает их извне (демо-компьютер, решение Свята 2026-10-30). По умолчанию выключено.
var allowExternalAdmin = app.Configuration.GetValue<bool>("Admin:AllowExternal");
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if ((path.StartsWithSegments("/admin") || path.StartsWithSegments("/_blazor"))
        && !allowExternalAdmin
        && !AdminAuth.IsLocalNetwork(context.Connection.RemoteIpAddress))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Пока база не готова, все страницы ведут на настройку подключения. Статические файлы (стили) отдаются как обычно.
app.Use(async (context, next) =>
{
    var store = context.RequestServices.GetRequiredService<DbConnectionStore>();
    var path = context.Request.Path;
    var endpoint = context.GetEndpoint();
    var isPage = endpoint is null || endpoint.Metadata.GetMetadata<ComponentTypeMetadata>() is not null
        || path.Equals("/robots.txt") || path.Equals("/sitemap.xml");
    if (store.State == DbState.Ready || !isPage || path.StartsWithSegments(DbConnectionStore.SetupPath))
    {
        try
        {
            await next();
        }
        catch (Exception ex) when (SiteCache.IsDatabaseUnavailable(ex) && !context.Response.HasStarted)
        {
            // База не ответила, а в кэше этой страницы ещё нет. Вместо ошибки 500 — понятная страница с автоповтором.
            app.Logger.LogWarning("Нет связи с базой при запросе {Path}: {Error}", path, ex.Message);
            await WriteWaitPage(context, 15, "Нет связи с базой данных", "Сайт не может получить данные. Попробуем снова через 15 секунд.");
        }
        return;
    }
    if (store.State == DbState.Connecting)
    {
        // Сразу после запуска: подключение к базе идёт в фоне.
        await WriteWaitPage(context, 5, "Сайт запускается", "Подключаемся к базе данных. Страница обновится сама.");
        return;
    }
    context.Response.Redirect(DbConnectionStore.SetupPath);
});

// Простая страница 503 без БД и без JavaScript: обновится сама через seconds секунд.
static Task WriteWaitPage(HttpContext context, int seconds, string title, string text)
{
    context.Response.Clear();
    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
    context.Response.Headers.RetryAfter = seconds.ToString();
    context.Response.ContentType = "text/html; charset=utf-8";
    return context.Response.WriteAsync(
        $"<!DOCTYPE html><html lang=\"ru\"><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"{seconds}\">" +
        $"<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>{title}</title></head>" +
        "<body style=\"font-family:sans-serif;max-width:560px;margin:15vh auto;padding:0 16px;color:#333\">" +
        $"<h1 style=\"font-weight:500\">{title}</h1><p>{text}</p></body></html>");
}

app.MapStaticAssets();
app.MapSeoEndpoints();
app.MapMediaEndpoints();
// Выход из админки: обычная форма POST с антифорджери-токеном (кнопка в шапке админки).
// Токен проверяем явно: у эндпоинта без привязки формы метаданные антифорджери запрос сами не отклоняют.
app.MapPost(AdminAuth.LogoutPath, async (HttpContext context, IAntiforgery antiforgery) =>
{
    if (!await antiforgery.IsRequestValidAsync(context))
        return Results.BadRequest();
    await context.SignOutAsync(AdminAuth.Scheme);
    return Results.LocalRedirect(AdminAuth.LoginPath);
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
