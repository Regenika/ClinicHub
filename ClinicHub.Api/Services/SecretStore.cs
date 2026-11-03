using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace ClinicHub.Api.Services;

/// <summary>
/// Секреты установленного сервера (#48): строка подключения, Jwt:Secret, Dikidi:* и т. п.
/// <para>
/// Установщик (deploy/api/install.ps1) кладёт их в <c>config\secrets.json</c>, доступный только SYSTEM,
/// администраторам и службе. При запуске сервер переносит их в <c>config\secrets.dat</c>, зашифрованный DPAPI
/// учётной записи службы (как подключение к базе у сайта), а открытый файл удаляет. В git и в журнал значения
/// не попадают — в журнал пишется только их число.
/// </para>
/// <para>
/// Пустое значение в secrets.json удаляет ключ (например, выключить Dikidi). При разработке файл не используется:
/// там user-secrets.
/// </para>
/// </summary>
public static class SecretStore
{
    public const string PlainFileName = "secrets.json";
    public const string ProtectedFileName = "secrets.dat";

    /// <summary>Папка секретов: Secrets:Directory, а у службы Windows — config рядом с сервером. null — не используем.</summary>
    public static string? DirectoryFor(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Secrets:Directory"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Path.Combine(env.ContentRootPath, configured));
        return WindowsServiceHelpers.IsWindowsService() ? Path.Combine(env.ContentRootPath, "config") : null;
    }

    /// <summary>Прочитать секреты (и принять новые из secrets.json). Ключи — как в конфигурации: «ConnectionStrings:ClinicHub».</summary>
    public static Dictionary<string, string?> Load(string directory, EventLog log)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory))
            return values;

        var protector = DataProtectionProvider
            .Create(new DirectoryInfo(Path.Combine(directory, "keys")), b =>
            {
                b.SetApplicationName("ClinicHub.Api");
                if (OperatingSystem.IsWindows())
                    b.ProtectKeysWithDpapi();
            })
            .CreateProtector("ClinicHub.Api.Secrets.v1");

        var protectedFile = Path.Combine(directory, ProtectedFileName);
        if (File.Exists(protectedFile))
        {
            var json = protector.Unprotect(File.ReadAllText(protectedFile, Encoding.UTF8));
            foreach (var (key, value) in Parse(json))
                values[key] = value;
        }

        var plainFile = Path.Combine(directory, PlainFileName);
        if (File.Exists(plainFile))
        {
            var incoming = Parse(File.ReadAllText(plainFile, Encoding.UTF8));
            foreach (var (key, value) in incoming)
            {
                if (string.IsNullOrEmpty(value))
                    values.Remove(key);
                else
                    values[key] = value;
            }

            File.WriteAllText(protectedFile, protector.Protect(JsonSerializer.Serialize(values)), Encoding.UTF8);
            File.Delete(plainFile);
            log.Write("Сервер", $"Секреты приняты из {PlainFileName} и зашифрованы (ключей: {incoming.Count}, всего: {values.Count}); открытый файл удалён.");
        }

        return values;
    }

    private static Dictionary<string, string?> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? new Dictionary<string, string?>();
}
