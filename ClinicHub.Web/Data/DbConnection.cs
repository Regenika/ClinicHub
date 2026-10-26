using System.Security.Cryptography;
using ClinicHub.Web.Site;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Web.Data;

/// <summary>Данные со страницы «Подключение к базе данных».</summary>
public sealed class DbConnectionForm
{
    /// <summary>Адрес SQL Server: «192.168.1.10\SQLEXPRESS», «sql-server,1433», «.\SQLEXPRESS».</summary>
    public string? Server { get; set; }
    public string? Database { get; set; } = "ClinicHub";
    /// <summary>Логин SQL Server. Пусто — вход Windows от имени учётной записи сайта.</summary>
    public string? Login { get; set; }
    public string? Password { get; set; }
    /// <summary>В сети клиники у SQL Server обычно самоподписанный сертификат, без этого шифрованное подключение не установится.</summary>
    public bool TrustServerCertificate { get; set; } = true;

    public string ToConnectionString()
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = Server?.Trim() ?? "",
            InitialCatalog = string.IsNullOrWhiteSpace(Database) ? "ClinicHub" : Database.Trim(),
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = 10,
            ApplicationName = "ClinicHub.Web",
        };
        if (string.IsNullOrWhiteSpace(Login))
        {
            b.IntegratedSecurity = true;
        }
        else
        {
            b.UserID = Login.Trim();
            b.Password = Password ?? "";
        }
        return b.ConnectionString;
    }

    /// <summary>Форма для повторного показа: адрес, база и логин, но не пароль.</summary>
    public static DbConnectionForm FromConnectionString(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        return new DbConnectionForm
        {
            Server = b.DataSource,
            Database = b.InitialCatalog,
            Login = b.IntegratedSecurity ? "" : b.UserID,
            TrustServerCertificate = b.TrustServerCertificate,
        };
    }
}

public enum DbState
{
    /// <summary>Подключение не задано: сайт показывает только страницу настройки.</summary>
    NotConfigured,
    /// <summary>Подключение задано, сайт подключается к базе (после запуска службы, в фоне). Посетители видят «подождите».</summary>
    Connecting,
    /// <summary>Подключение задано, но база недоступна. Сайт пробует снова каждые 30 секунд.</summary>
    Failed,
    Ready,
}

/// <summary>
/// Подключение к базе сайта. Источник по приоритету:
///   1) ConnectionStrings:ClinicHub из user-secrets или переменной окружения (разработка, ADR-0009);
///   2) файл config\db-connection.dat, который пишет страница /nastroika-bd (компьютер клиники).
/// Файл зашифрован ASP.NET Data Protection, ключи — в config\keys под защитой DPAPI учётной записи сайта:
/// прочитать пароль может только сама служба сайта на этом компьютере.
/// </summary>
public sealed class DbConnectionStore(
    IConfiguration config,
    IHostEnvironment env,
    IDataProtectionProvider dataProtection,
    ILogger<DbConnectionStore> logger)
{
    public const string SetupPath = "/nastroika-bd";

    private readonly IDataProtector _protector = dataProtection.CreateProtector("ClinicHub.Web.DbConnection");
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Строка задана в конфигурации — менять её со страницы нельзя.</summary>
    public bool IsFromConfiguration { get; private set; }
    public string? ConnectionString { get; private set; }
    public DbState State { get; private set; } = DbState.NotConfigured;
    /// <summary>Почему база недоступна — для страницы настройки и журнала.</summary>
    public string? LastError { get; private set; }

    public string SettingsFile => Path.Combine(ConfigDirectory(config, env), "db-connection.dat");

    /// <summary>Папка с настройками, которые сайт пишет сам. Служба может писать только сюда и в logs.</summary>
    public static string ConfigDirectory(IConfiguration config, IHostEnvironment env) =>
        Path.Combine(env.ContentRootPath, config["Database:ConfigDirectory"] ?? "config");

    /// <summary>
    /// После запуска, в фоне (<see cref="DbConnectionMonitor"/>): прочитать подключение и подготовить базу. Ошибки не роняют сайт.
    /// Не до старта сайта: служба Windows обязана ответить системе за 30 секунд, а база по интернету может отвечать дольше.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var fromConfig = config.GetConnectionString("ClinicHub");
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            IsFromConfiguration = true;
            ConnectionString = fromConfig;
        }
        else
        {
            ConnectionString = ReadFile();
        }

        if (ConnectionString is null)
        {
            logger.LogWarning("Подключение к базе не задано. Откройте на этом компьютере http://localhost:<порт>{Path}", SetupPath);
            return;
        }
        State = DbState.Connecting;
        logger.LogInformation("Подключаюсь к базе: сервер {Server}", new SqlConnectionStringBuilder(ConnectionString).DataSource);
        await ActivateAsync(ConnectionString, ct);
        if (State == DbState.Ready)
            logger.LogInformation("База сайта готова");
    }

    /// <summary>Повторная попытка, если база была недоступна (например, SQL Server запустился позже сайта).</summary>
    public async Task RetryAsync(CancellationToken ct)
    {
        if (State == DbState.Failed && ConnectionString is { } cs)
            await ActivateAsync(cs, ct);
    }

    /// <summary>Проверить подключение, подготовить базу и только потом сохранить. Возвращает текст ошибки или null.</summary>
    public async Task<string?> SaveAsync(DbConnectionForm form, CancellationToken ct = default)
    {
        if (IsFromConfiguration)
            return "Подключение задано в настройках сайта (ConnectionStrings:ClinicHub), со страницы его поменять нельзя.";

        string cs;
        try
        {
            cs = form.ToConnectionString();
        }
        catch (ArgumentException ex)
        {
            return "Неверные данные подключения: " + ex.Message;
        }

        // Повторная отправка того же (двойной клик, «Обновить» после POST): подключение уже сохранено и работает.
        if (cs == ConnectionString && State == DbState.Ready)
            return null;

        var error = await PrepareDatabaseAsync(cs, ct);
        if (error is not null)
            return error;

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
        await File.WriteAllTextAsync(SettingsFile, _protector.Protect(cs), ct);
        ConnectionString = cs;
        State = DbState.Ready;
        LastError = null;
        var b = new SqlConnectionStringBuilder(cs);
        logger.LogInformation("Подключение к базе сохранено: сервер {Server}, база {Database}, {Auth}",
            b.DataSource, b.InitialCatalog, b.IntegratedSecurity ? "вход Windows" : "логин " + b.UserID);
        return null;
    }

    private async Task ActivateAsync(string cs, CancellationToken ct)
    {
        var error = await PrepareDatabaseAsync(cs, ct);
        if (error is null)
        {
            State = DbState.Ready;
            LastError = null;
            return;
        }
        if (State != DbState.Failed || LastError != error)
            logger.LogError("База сайта недоступна: {Error}. Повтор через 30 секунд", error);
        State = DbState.Failed;
        LastError = error;
    }

    /// <summary>Миграции схемы site и начальное наполнение. Одновременно — не больше одной подготовки.</summary>
    private async Task<string?> PrepareDatabaseAsync(string cs, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var content = config.GetSection("Site").Get<SiteContent>() ?? new SiteContent();
            await SiteSeeder.MigrateAndSeedAsync(cs, content, logger, ct);
            return null;
        }
        catch (SqlException ex)
        {
            return Explain(ex);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is SqlException sql)
        {
            return Explain(sql);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Не сеть и не права: например, сборка сайта не совпадает со схемой базы. Сайт не падает
            // (иначе служба уйдёт в цикл перезапусков), а показывает причину на странице настройки.
            logger.LogError(ex, "Не удалось подготовить базу сайта");
            return "Не удалось подготовить базу: " + ex.Message;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? ReadFile()
    {
        if (!File.Exists(SettingsFile))
            return null;
        try
        {
            return _protector.Unprotect(File.ReadAllText(SettingsFile));
        }
        catch (CryptographicException ex)
        {
            // Ключи шифрования потеряны (папку config\keys удалили или сайт запущен под другой учётной записью).
            logger.LogError(ex, "Не удалось расшифровать {File}. Задайте подключение заново на странице {Path}", SettingsFile, SetupPath);
            LastError = "Сохранённое подключение не удалось расшифровать. Введите данные заново.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Например, у службы нет прав на папку config. Сайт не падает, а показывает причину на странице настройки.
            logger.LogError(ex, "Не удалось прочитать {File}", SettingsFile);
            LastError = $"Не удалось прочитать сохранённое подключение ({ex.Message}). Проверьте права службы на папку config или введите данные заново.";
            return null;
        }
    }

    /// <summary>Понятная причина по коду ошибки SQL Server; исходный текст — в скобках, для разработчика.</summary>
    private static string Explain(SqlException ex)
    {
        var reason = ex.Number switch
        {
            18456 => "Неверный логин или пароль, или для логина не включён вход по паролю на сервере.",
            18452 => "Сервер не принимает вход Windows от этой учётной записи. Укажите логин и пароль SQL Server.",
            4060 => "У логина нет доступа к этой базе.",
            262 or 1802 => "Базы ещё нет, а создать её у логина нет прав. Создайте базу на сервере или дайте логину право dbcreator.",
            // Сетевые ошибки: имя не найдено, экземпляр не найден, порт закрыт, таймаут.
            -1 or 2 or 26 or 40 or 53 or 64 or 121 or 258 or 1225 or 10060 or 10061 or 11001 =>
                "Сервер не найден или не отвечает. Проверьте адрес, что SQL Server запущен, в нём включён TCP/IP, а порт открыт в брандмауэре.",
            -2146893019 => "Сервер предъявил сертификат, которому компьютер не доверяет. Включите «Доверять сертификату сервера».",
            _ => "Не удалось подключиться к базе.",
        };
        return $"{reason} ({ex.Message})";
    }
}

/// <summary>Контексты БД из текущего подключения: после сохранения на странице настройки перезапуск не нужен.</summary>
public sealed class SiteDbContextFactory(DbConnectionStore store) : IDbContextFactory<SiteDbContext>
{
    private (string ConnectionString, DbContextOptions<SiteDbContext> Options)? _cached;

    public SiteDbContext CreateDbContext()
    {
        var cs = store.ConnectionString ?? throw new InvalidOperationException("Подключение к базе сайта не задано.");
        var cached = _cached;
        if (cached is null || cached.Value.ConnectionString != cs)
        {
            var builder = new DbContextOptionsBuilder<SiteDbContext>();
            SiteDbContext.Configure(builder, cs);
            cached = _cached = (cs, builder.Options);
        }
        return new SiteDbContext(cached.Value.Options);
    }
}

/// <summary>Подключается к базе после запуска сайта, а если база недоступна — пробует снова, пока не получится.</summary>
public sealed class DbConnectionMonitor(DbConnectionStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Отпускаем запуск сайта сразу: всё дальнейшее — в фоне.
        await Task.Yield();
        await store.InitializeAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await store.RetryAsync(stoppingToken);
    }
}
