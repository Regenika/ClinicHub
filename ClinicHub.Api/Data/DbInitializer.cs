using ClinicHub.Api.Services;
using ClinicHub.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(ClinicHubDbContext db, ILogger logger, IConfiguration? config = null)
    {
        // Схема создаётся и обновляется только миграциями. EnsureCreated не используем:
        // базу, созданную им, потом нельзя обновить миграцией.
        await db.Database.MigrateAsync();

        await SeedAsync(db, logger, config);
        await Modules.Catalog.CatalogSeed.SeedAsync(db, logger);
        await Modules.Messaging.Scenarios.ScenarioSeed.SeedAsync(db, logger);
    }

    /// <summary>12 символов без похожих друг на друга (0/O, 1/l/I), чтобы пароль было легко переписать с экрана.</summary>
    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return System.Security.Cryptography.RandomNumberGenerator.GetString(alphabet, 12);
    }

    private static async Task SeedAsync(ClinicHubDbContext db, ILogger logger, IConfiguration? config)
    {
        // Первый администратор. Пароля по умолчанию больше нет: он либо задан явно
        // (переменная SEED_ADMIN_PASSWORD или Seed:AdminPassword в user-secrets), либо генерируется случайный
        // и один раз выводится в журнал. Создаётся только в пустой базе.
        if (!await db.Users.AnyAsync())
        {
            var salt = PasswordHasher.GenerateSalt();
            var login = Environment.GetEnvironmentVariable("SEED_ADMIN_LOGIN") ?? config?["Seed:AdminLogin"] ?? "admin";
            var password = Environment.GetEnvironmentVariable("SEED_ADMIN_PASSWORD") ?? config?["Seed:AdminPassword"];
            var generated = string.IsNullOrWhiteSpace(password);
            if (generated)
                password = GeneratePassword();

            db.Users.Add(new User
            {
                Login = login,
                Salt = salt,
                PasswordHash = PasswordHasher.Hash(password!, salt),
                Role = UserRole.Admin,
            });
            if (generated)
                logger.LogWarning("Создан первый администратор: логин {Login}, пароль {Password}. " +
                                  "Запишите пароль: больше он нигде не показывается.", login, password);
            else
                logger.LogWarning("Создан первый администратор: логин {Login}, пароль из настроек.", login);
        }

        // Активная конфигурация лимитов.
        if (!await db.RateConfigs.AnyAsync())
        {
            db.RateConfigs.Add(new RateConfig
            {
                Name = "Основная",
                SendWindowFrom = new TimeSpan(9, 0, 0),
                SendWindowTo = new TimeSpan(20, 0, 0),
                DefaultPerMinute = 1,
                DefaultPerHour = 60,
                DefaultPerDay = 300,
                MinIntervalSeconds = 5,
                IsActive = true,
            });
        }

        // Системные настройки.
        var settings = new Dictionary<string, string>
        {
            ["MaxAttempts"] = "3",
            ["WebhookApiKey"] = Environment.GetEnvironmentVariable("WEBHOOK_API_KEY") ?? Guid.NewGuid().ToString("N"),
            ["WebhookCallbackUrl"] = "",
        };

        foreach (var (key, value) in settings)
        {
            if (await db.AppSettings.FindAsync(key) is null)
                db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        }

        // Шаблоны сообщений — перенесены из исходного Flutter-приложения (4 захардкоженных
        // паттерна) как отправная точка; дальше редактируются в админке без изменения кода.
        if (!await db.MessageTemplates.AnyAsync())
        {
            db.MessageTemplates.AddRange(
                new MessageTemplate
                {
                    Name = "Не забудьте о приёме",
                    Text = "Уважаемый пациент, не забудьте о приёме к врачу {doctor}, кабинет {cabinet}.",
                },
                new MessageTemplate
                {
                    Name = "Не забудьте записаться к врачу",
                    Text = "Уважаемый пациент, не забудьте записаться к врачу {doctor}.",
                },
                new MessageTemplate
                {
                    Name = "Напоминаем о приёме",
                    Text = "Напоминаем о предстоящем приёме к врачу {doctor}, кабинет {cabinet}, {date} в {time}.",
                },
                new MessageTemplate
                {
                    Name = "Подтвердите запись",
                    Text = "Пожалуйста, подтвердите запись к врачу {doctor} на {date} в {time}.",
                });
        }

        // Тестовый получатель: ваш собственный номер из настроек (Seed:MyPhone в appsettings.json или
        // переменная SEED_MY_PHONE) — создаётся пациент с этим номером, чтобы слать SMS самому себе.
        // Телефон-отправитель создавать не нужно: он появляется сам, когда в приложении SmsAgent
        // нажимают «Подключиться». Если номер не задан, ничего не добавляется.
        var myPhone = (config?["Seed:MyPhone"] ?? Environment.GetEnvironmentVariable("SEED_MY_PHONE") ?? "").Trim();
        if (myPhone.Length > 0)
        {
            if (!await db.Patients.AnyAsync(p => p.Phone == myPhone))
            {
                db.Patients.Add(new Patient
                {
                    FullName = "Я (тестовый получатель)",
                    Phone = myPhone,
                    IsSubscribed = true,
                });
            }
        }

        // Уборка тестового мусора: телефон с пустым идентификатором (остался от ранних версий, где номер
        // вводили вручную). Его SIM и остатки удаляются; записи очереди и журнала отправок остаются,
        // только теряют привязку к удалённому телефону.
        var broken = await db.Phones.Include(p => p.SimCards).Where(p => p.PhoneNumber == "").ToListAsync();
        foreach (var ph in broken)
        {
            var simIds = ph.SimCards.Select(s => s.Id).ToList();
            var queue = await db.SmsQueue
                .Where(q => q.PhoneId == ph.Id || (q.SimCardId != null && simIds.Contains(q.SimCardId.Value)))
                .ToListAsync();
            foreach (var q in queue)
            {
                q.PhoneId = null;
                q.SimCardId = null;
            }

            var logs = await db.SmsLogs
                .Where(l => l.PhoneId == ph.Id || (l.SimCardId != null && simIds.Contains(l.SimCardId.Value)))
                .ToListAsync();
            foreach (var l in logs)
            {
                l.PhoneId = null;
                l.SimCardId = null;
            }

            db.Phones.Remove(ph);
            logger.LogWarning("Удалён тестовый телефон #{Id} с пустым идентификатором (SIM: {Sims})",
                ph.Id, string.Join(", ", ph.SimCards.Select(s => s.Number)));
        }

        await db.SaveChangesAsync();

        var webhookKey = settings["WebhookApiKey"];
        if (!string.IsNullOrWhiteSpace(webhookKey))
            logger.LogInformation("Webhook API-ключ для сайта клиники: {Key}", webhookKey);
    }
}