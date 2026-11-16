using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using ClinicHub.Api.Data;
using ClinicHub.Core.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClinicHub.Api.Services;

/// <summary>
/// Вход веб-панели рассылок (ClinicHub.Web) в API — ADR-0012. Сайт и сервер стоят на одном компьютере, поэтому
/// доверие строится на общем секрете в файле, который видят только они: сервер создаёт ключ при первом запуске,
/// сайт читает тот же файл. Запрос с ключом принимается только с этого же компьютера (loopback).
/// Сотрудника сайт проверяет сам (учётки сайта), а серверу передаёт его логин — для журнала.
/// </summary>
public static class PanelAuth
{
    public const string Scheme = "PanelKey";
    public const string KeyHeader = "X-Panel-Key";
    public const string UserHeader = "X-Panel-User";
    /// <summary>Служебный пользователь сервера, от имени которого действует панель (CreatedById и т. п.).</summary>
    public const string ServiceLogin = "web-panel";

    /// <summary>Файл ключа: Panel:KeyFile, по умолчанию %ProgramData%\ClinicHub\panel.key.</summary>
    public static string KeyFilePath(IConfiguration config) =>
        config["Panel:KeyFile"] is { Length: > 0 } path
            ? path
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClinicHub", "panel.key");
}

/// <summary>Ключ панели: читается из файла, а если файла нет — создаётся (только сервером).</summary>
public sealed class PanelKeyStore(IConfiguration config, ILogger<PanelKeyStore> logger)
{
    private string? _key;
    private bool _loaded;
    private readonly Lock _gate = new();

    public string? Key
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded)
                {
                    _key = LoadOrCreate();
                    _loaded = true;
                }
                return _key;
            }
        }
    }

    private string? LoadOrCreate()
    {
        var path = PanelAuth.KeyFilePath(config);
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 32)
                    return existing;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            File.WriteAllText(path, key);
            logger.LogInformation("Создан ключ веб-панели: {Path}", path);
            return key;
        }
        catch (Exception ex)
        {
            // Без ключа панель просто не войдёт; JWT (программа администратора, телефоны) работает как раньше.
            logger.LogWarning("Ключ веб-панели недоступен ({Path}): {Error}. Панель рассылок на сайте работать не будет.", path, ex.Message);
            return null;
        }
    }
}

public sealed class PanelAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory loggerFactory, UrlEncoder encoder,
    PanelKeyStore keys, IServiceScopeFactory scopes)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private static int? _serviceUserId;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(PanelAuth.KeyHeader, out var given) || string.IsNullOrEmpty(given))
            return AuthenticateResult.NoResult();

        var ip = Context.Connection.RemoteIpAddress;
        if (ip is not null && !System.Net.IPAddress.IsLoopback(ip))
            return AuthenticateResult.Fail("Ключ панели принимается только с этого компьютера.");

        var key = keys.Key;
        if (key is null || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(key), System.Text.Encoding.UTF8.GetBytes(given.ToString())))
            return AuthenticateResult.Fail("Неверный ключ панели.");

        var userId = _serviceUserId ??= await EnsureServiceUserAsync();
        // Логин сотрудника приходит от сайта URL-кодированным (кириллица в заголовке).
        var acting = Uri.UnescapeDataString(Request.Headers[PanelAuth.UserHeader].ToString());
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(acting) ? PanelAuth.ServiceLogin : acting),
            new Claim(ClaimTypes.Role, nameof(UserRole.Admin)),
        ], PanelAuth.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), PanelAuth.Scheme));
    }

    /// <summary>Служебный пользователь: выключен (по паролю под ним не войти), нужен как автор заявок.</summary>
    private async Task<int> EnsureServiceUserAsync()
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHubDbContext>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Login == PanelAuth.ServiceLogin);
        if (user is not null)
            return user.Id;

        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        user = new User
        {
            Login = PanelAuth.ServiceLogin,
            Salt = salt,
            PasswordHash = PasswordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), salt),
            Role = UserRole.Admin,
            IsActive = false,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
