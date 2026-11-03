using System.Text.Encodings.Web;
using System.Text.Unicode;
using ClinicHub.Web.Booking;
using ClinicHub.Web.Components;
using ClinicHub.Web.Site;
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

// Контент сайта (клиника, услуги, врачи) — отдельный файл, правится без пересборки.
builder.Configuration.AddJsonFile("site-content.json", optional: false, reloadOnChange: true);
builder.Services.Configure<SiteContent>(builder.Configuration.GetSection("Site"));

// Кириллицу в HTML отдаём как есть, а не &#x...; — страницы короче и читаемее. Опасные символы (<, >, &, ") по-прежнему экранируются.
builder.Services.AddWebEncoders(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddSingleton(TimeProvider.System);
// Пока МИС не выбрана — демо-слоты. Потом здесь будет адаптер МИС (PLAN, этап 2).
builder.Services.AddSingleton<IBookingSource, DemoBookingSource>();

// Публичная часть — статический SSR без интерактивности: быстро и индексируется (ADR-0003).
builder.Services.AddRazorComponents();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapSeoEndpoints();
app.MapRazorComponents<App>();

app.Run();
