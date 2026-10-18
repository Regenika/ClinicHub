using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using ClinicHub.Web.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Web.Admin;

public static class AdminAuth
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string LoginPath = "/admin/vhod";
    public const string FirstEditorPath = "/admin/pervyi-redaktor";
    public const string LogoutPath = "/admin/vyhod";
    public const int MinPasswordLength = 10;

    public static ClaimsPrincipal Principal(Editor e) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, e.Id.ToString()),
        new Claim(ClaimTypes.Name, e.Login),
        new Claim("display_name", e.DisplayName),
    ], Scheme));

    /// <summary>Логин из куки — им подписываются изменения (история цен, публикации).</summary>
    public static string LoginOf(ClaimsPrincipal user) => user.Identity?.Name ?? "?";

    /// <summary>
    /// Админка открывается только из сети клиники (ADR-0001, ADR-0007 §5): этот компьютер, локальные сети, VPN.
    /// Адреса из интернета получают 404, даже с правильным паролем.
    /// </summary>
    public static bool IsLocalNetwork(IPAddress? ip)
    {
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6SiteLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }
}

/// <summary>Защита входа от перебора: после 5 неудачных попыток для пары «логин + адрес» — пауза 5 минут.</summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    private const int MaxFailures = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(5);

    private sealed record State(int Failures, DateTimeOffset LastFailure);
    private readonly ConcurrentDictionary<string, State> _state = new();

    public TimeSpan? LockedFor(string key)
    {
        if (!_state.TryGetValue(key, out var s) || s.Failures < MaxFailures) return null;
        var left = s.LastFailure + Lockout - clock.GetUtcNow();
        if (left > TimeSpan.Zero) return left;
        _state.TryRemove(key, out _);
        return null;
    }

    public void Failed(string key) =>
        _state.AddOrUpdate(key, _ => new State(1, clock.GetUtcNow()), (_, s) => new State(s.Failures + 1, clock.GetUtcNow()));

    public void Succeeded(string key) => _state.TryRemove(key, out _);
}

/// <summary>Учётные записи редакторов: вход, первый редактор, добавление, отключение, смена пароля.</summary>
public sealed class EditorAccounts(IDbContextFactory<SiteDbContext> dbFactory, TimeProvider clock, ILogger<EditorAccounts> logger)
{
    private readonly PasswordHasher<Editor> _hasher = new();

    public static string NormalizeLogin(string? login) => (login ?? "").Trim().ToLowerInvariant();

    public async Task<bool> AnyAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Editors.AnyAsync();
    }

    /// <summary>Редактор, если логин и пароль верны и учётная запись активна; иначе null.</summary>
    public async Task<Editor?> VerifyAsync(string? login, string? password)
    {
        var norm = NormalizeLogin(login);
        await using var db = await dbFactory.CreateDbContextAsync();
        var editor = await db.Editors.FirstOrDefaultAsync(e => e.Login == norm);
        if (editor is null || !editor.IsActive || string.IsNullOrEmpty(password))
        {
            // Хешируем впустую, чтобы по времени ответа нельзя было понять, существует ли логин.
            _hasher.HashPassword(new Editor(), password ?? "");
            return null;
        }

        var result = _hasher.VerifyHashedPassword(editor, editor.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return null;
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
            editor.PasswordHash = _hasher.HashPassword(editor, password);
        editor.LastLoginAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
        return editor;
    }

    /// <summary>Ошибка в данных нового редактора или null.</summary>
    public static string? Validate(string? login, string? displayName, string? password, string? repeat)
    {
        var norm = NormalizeLogin(login);
        if (norm.Length is < 3 or > 50 || !norm.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            return "Логин: от 3 до 50 символов, латинские буквы, цифры, точка, дефис, подчёркивание.";
        if (string.IsNullOrWhiteSpace(displayName))
            return "Укажите имя — его видно в истории изменений.";
        return ValidatePassword(password, repeat);
    }

    public static string? ValidatePassword(string? password, string? repeat)
    {
        if (string.IsNullOrEmpty(password) || password.Length < AdminAuth.MinPasswordLength)
            return $"Пароль — не короче {AdminAuth.MinPasswordLength} символов.";
        if (password != repeat)
            return "Пароли не совпадают.";
        return null;
    }

    /// <summary>Создать редактора. onlyIfNone — для первого редактора: не создаёт, если кто-то уже есть.</summary>
    public async Task<(Editor? Editor, string? Error)> CreateAsync(string login, string displayName, string password, bool onlyIfNone, string createdBy)
    {
        var norm = NormalizeLogin(login);
        await using var db = await dbFactory.CreateDbContextAsync();
        if (onlyIfNone && await db.Editors.AnyAsync())
            return (null, "Первый редактор уже создан. Войдите под ним.");
        if (await db.Editors.AnyAsync(e => e.Login == norm))
            return (null, "Такой логин уже есть.");

        var editor = new Editor
        {
            Login = norm,
            DisplayName = displayName.Trim(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        editor.PasswordHash = _hasher.HashPassword(editor, password);
        db.Editors.Add(editor);
        await db.SaveChangesAsync();
        logger.LogInformation("Создан редактор сайта {Login} (создал: {CreatedBy})", norm, createdBy);
        return (editor, null);
    }

    public async Task SetPasswordAsync(int editorId, string password, string changedBy)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var editor = await db.Editors.FirstAsync(e => e.Id == editorId);
        editor.PasswordHash = _hasher.HashPassword(editor, password);
        await db.SaveChangesAsync();
        logger.LogInformation("Пароль редактора {Login} изменён (изменил: {ChangedBy})", editor.Login, changedBy);
    }

    /// <summary>Включить или отключить. Последнего активного редактора отключить нельзя — иначе в админку не войти.</summary>
    public async Task<string?> SetActiveAsync(int editorId, bool active, string changedBy)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var editor = await db.Editors.FirstAsync(e => e.Id == editorId);
        if (!active && editor.IsActive && await db.Editors.CountAsync(e => e.IsActive) <= 1)
            return "Нельзя отключить последнего активного редактора.";
        editor.IsActive = active;
        await db.SaveChangesAsync();
        logger.LogInformation("Редактор {Login} {State} (изменил: {ChangedBy})", editor.Login, active ? "включён" : "отключён", changedBy);
        return null;
    }

    public async Task<List<Editor>> ListAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Editors.AsNoTracking().OrderBy(e => e.Login).ToListAsync();
    }

    /// <summary>Отключённого после входа редактора выкидываем при следующем запросе (проверка в куке).</summary>
    public async Task<bool> IsActiveAsync(int editorId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Editors.AnyAsync(e => e.Id == editorId && e.IsActive);
    }
}
