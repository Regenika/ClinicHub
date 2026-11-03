using System.Text;
using System.Text.Json.Serialization;
using ClinicHub.Api.Background;
using ClinicHub.Api.Data;
using ClinicHub.Api.Endpoints;
using ClinicHub.Api.Modules.Catalog;
using ClinicHub.Api.Modules.Consents;
using ClinicHub.Api.Modules.Integrations.Dikidi;
using ClinicHub.Api.Modules.Messaging.Scenarios;
using ClinicHub.Api.Modules.Visits;
using ClinicHub.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// ── Журнал событий (файл logs/clinichub.log рядом с проектом) ───────────────
var eventLog = new EventLog(builder.Environment.ContentRootPath);
builder.Services.AddSingleton(eventLog);
builder.Logging.AddProvider(new EventLogLoggerProvider(eventLog));
eventLog.Write("Сервер", "=== Запуск ClinicHub.Api ===");

// ── Конфигурация ────────────────────────────────────────────────────────────
// Секреты (JWT, пароль БД) в git не лежат: на компьютере разработчика — dotnet user-secrets,
// на сервере клиники — переменные окружения. Как задать — README, раздел 1.
var jwt = builder.Configuration.GetSection("Jwt").Get<TokenOptions>() ?? new TokenOptions();
if (!string.IsNullOrEmpty(builder.Configuration["JWT_SECRET"]))
    jwt.Secret = builder.Configuration["JWT_SECRET"]!;
if (Encoding.UTF8.GetByteCount(jwt.Secret) < 32)
{
    if (!builder.Environment.IsDevelopment())
        StartupError.Fail(eventLog,
            "Не задан секрет для токенов входа (Jwt:Secret, не короче 32 символов). " +
            "Задайте переменную окружения Jwt__Secret или JWT_SECRET и перезапустите сервер.");

    // При разработке не мешаем запуску: случайный секрет на время работы сервера.
    // После перезапуска Admin попросит войти заново.
    jwt.Secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    eventLog.Write("Сервер", "Jwt:Secret не задан — взят случайный на этот запуск (только Development).");
}
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton(new IntegrationOptions
{
    WebhookApiKey = builder.Configuration["Integration:WebhookApiKey"] ?? "",
});

// ── БД (EF Core) ────────────────────────────────────────────────────────────
// Сейчас SQL Server, перед продом — PostgreSQL (ADR-0009). Код запросов пишем переносимо: только LINQ.
// Схемы site и catalog занимают сайт и каталог (ADR-0007, ADR-0008), таблицы сервера — в схеме по умолчанию.
var connectionString = builder.Configuration.GetConnectionString("ClinicHub");
if (string.IsNullOrWhiteSpace(connectionString))
    StartupError.Fail(eventLog,
        "Не задана строка подключения к базе (ConnectionStrings:ClinicHub). " +
        "На компьютере разработчика: dotnet user-secrets set \"ConnectionStrings:ClinicHub\" " +
        "\"Server=(localdb)\\MSSQLLocalDB;Database=ClinicHub;Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=True\" " +
        "(или Server=.\\SQLEXPRESS для SQL Server Express). На сервере: переменная окружения ConnectionStrings__ClinicHub.");
builder.Services.AddDbContext<ClinicHubDbContext>(options => options.UseSqlServer(connectionString ?? ""));

// ── Сервисы ─────────────────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SchedulerService>();
builder.Services.AddScoped<WebhookNotifier>();
builder.Services.AddScoped<AutoRuleService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<VisitEvents>();
// Согласия (#34): единственная проверка «можно ли писать пациенту» для заявок, авто-правил и напоминаний.
builder.Services.AddScoped<IConsentChecker, ConsentChecker>();
// Сценарии напоминаний (#14): планировщик подписан на «визит завершён», отправка — фоновым воркером.
builder.Services.AddScoped<ScenarioPlanner>();
builder.Services.AddScoped<IVisitCompletedHandler>(sp => sp.GetRequiredService<ScenarioPlanner>());
builder.Services.AddScoped<ReminderDispatcher>();
builder.Services.AddHostedService<ReminderWorker>();
// Адаптер Dikidi (#15): только чтение журнала. Без логина и пароля в секретах — выключен.
var dikidi = builder.Configuration.GetSection("Dikidi").Get<DikidiOptions>() ?? new DikidiOptions();
builder.Services.AddSingleton(dikidi);
builder.Services.AddSingleton(new DikidiSyncState { Configured = dikidi.IsConfigured });
builder.Services.AddSingleton<IDikidiClient, DikidiWebClient>();
builder.Services.AddScoped<DikidiSync>();
builder.Services.AddHostedService<DikidiWorker>();
builder.Services.AddHostedService<SchedulerWorker>();
builder.Services.AddHostedService<AutoRuleWorker>();
builder.Services.AddHostedService<LanDiscoveryService>();
builder.Services.AddHostedService<DiagnosticsWorker>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ── Аутентификация JWT ──────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddOpenApi(o =>
{
    o.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "ClinicHub SMS API";
        document.Info.Description = "Серверная часть системы рассылки SMS клиники.";
        document.Info.Version = "v1";
        return Task.CompletedTask;
    });
});

var app = builder.Build();

// ── Инициализация БД и seed ─────────────────────────────────────────────────
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ClinicHubDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInit");
    await DbInitializer.InitializeAsync(db, logger, app.Configuration);
}

// ── Конвейер запросов ───────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();

// Журнал запросов от телефонов: видно, кто и когда обращался и чем это закончилось.
// Успешные «пульс» и пустые опросы задач не пишем — их слишком много.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "";
    if (!path.StartsWith("/api/agent", StringComparison.OrdinalIgnoreCase))
    {
        await next();
        return;
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next();

    var ok = ctx.Response.StatusCode is >= 200 and < 300;
    var quiet = ok && (path.EndsWith("/heartbeat", StringComparison.OrdinalIgnoreCase)
                       || path.EndsWith("/log", StringComparison.OrdinalIgnoreCase)
                       || (ctx.Request.Method == "GET" && path.EndsWith("/tasks", StringComparison.OrdinalIgnoreCase)));
    if (!quiet)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is not null && ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        eventLog.Write("HTTP", $"{ctx.Request.Method} {path} → {ctx.Response.StatusCode} ({sw.ElapsedMilliseconds} мс) от {ip}");
    }
});
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapPatientsEndpoints();
app.MapRequestsEndpoints();
app.MapPhonesEndpoints();
app.MapAgentEndpoints();
app.MapWebhookEndpoints();
app.MapDashboardEndpoints();
app.MapAutoRulesEndpoints();
app.MapMessageTemplatesEndpoints();
app.MapCatalogEndpoints();
app.MapVisitEndpoints();
app.MapConsentEndpoints();
app.MapDikidiEndpoints();
app.MapScenarioEndpoints();

// Отладка: только с самого компьютера, где запущен сервер (в журнале есть номера и тексты SMS).
app.MapGet("/api/debug/log", (HttpContext ctx, int? tail) =>
    IsLocal(ctx)
        ? Results.Text(string.Join(Environment.NewLine, eventLog.Tail(tail ?? 300)), "text/plain; charset=utf-8")
        : Results.NotFound()).AllowAnonymous();

app.MapGet("/api/debug/state", async (HttpContext ctx, ClinicHubDbContext db, DikidiSyncState dikidiState, CancellationToken ct) =>
    IsLocal(ctx)
        ? Results.Text(await DiagnosticsWorker.BuildAsync(db, detailed: true, ct, dikidiState), "text/plain; charset=utf-8")
        : Results.NotFound()).AllowAnonymous();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }))
    .AllowAnonymous();

app.Run();

static bool IsLocal(HttpContext ctx)
{
    var ip = ctx.Connection.RemoteIpAddress;
    return ip is null || System.Net.IPAddress.IsLoopback(ip);
}

/// <summary>Понятная остановка при неверной настройке вместо трассировки стека.</summary>
static class StartupError
{
    public static void Fail(EventLog log, string message)
    {
        // dotnet ef (создание миграций) тоже запускает Program.cs, но секреты ему не нужны.
        if (EF.IsDesignTime)
            return;

        log.Write("Сервер", "ОШИБКА НАСТРОЙКИ: " + message);
        Console.Error.WriteLine("ОШИБКА НАСТРОЙКИ: " + message);
        Environment.Exit(1);
    }
}
