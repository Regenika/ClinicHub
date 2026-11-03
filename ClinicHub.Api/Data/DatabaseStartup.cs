using ClinicHub.Api.Services;
using Microsoft.Data.SqlClient;

namespace ClinicHub.Api.Data;

/// <summary>
/// Готова ли база (миграции применены, начальные данные созданы). Фоновые процессы ждут её здесь,
/// а не падают с ошибкой каждые несколько секунд, пока базы нет.
/// </summary>
public sealed class DatabaseReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompleted;

    /// <summary>Последняя причина, по которой подключиться не удалось (для /api/debug/state; в ответах наружу не отдаём).</summary>
    public string? LastError { get; private set; }

    public void MarkReady()
    {
        LastError = null;
        _ready.TrySetResult();
    }

    public void SetError(string error) => LastError = error;

    /// <summary>Ждать готовности базы. false — сервер останавливается, дальше работать не нужно.</summary>
    public async Task<bool> WaitAsync(CancellationToken ct)
    {
        try
        {
            await _ready.Task.WaitAsync(ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

/// <summary>
/// Подключение к базе после запуска сервера (#48). На компьютере клиники база может быть на другом компьютере
/// (у Свята дома, через VPN) и временно недоступна. Раньше сервер в таком случае падал при запуске; теперь он
/// запускается, отвечает на /api/health и раз в 30 секунд пробует снова, записывая понятную причину в журнал.
/// Служба Windows к тому же обязана ответить системе за 30 секунд — ждать базу до старта нельзя.
/// </summary>
public sealed class DatabaseStartupService(
    IServiceScopeFactory scopes, DatabaseReadiness readiness, IConfiguration config, EventLog eventLog,
    ILogger<DatabaseStartupService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        string? lastLogged = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ClinicHubDbContext>();
                await DbInitializer.InitializeAsync(db, logger, config);
                readiness.MarkReady();
                if (lastLogged is not null)
                    eventLog.Write("Сервер", "Связь с базой данных есть, сервер работает.");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var reason = Explain(ex);
                readiness.SetError(reason);
                // Одна и та же причина — одна строка в журнале, а не каждые 30 секунд.
                if (reason != lastLogged)
                {
                    logger.LogError("Нет связи с базой данных: {Reason} Сервер работает и пробует снова каждые 30 секунд.", reason);
                    lastLogged = reason;
                }
            }

            try { await Task.Delay(RetryEvery, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Причина человеческими словами. Строку подключения и пароль не выводим.</summary>
    internal static string Explain(Exception ex)
    {
        var sql = ex as SqlException ?? ex.InnerException as SqlException;
        return sql?.Number switch
        {
            // 10061 — компьютер отклонил подключение (SQL Server не слушает этот порт), 11001/11004 — имя не найдено.
            -1 or 53 or 2 or 40 or 10061 or 11001 or 11004 => "сервер базы данных не найден или недоступен по сети (проверьте адрес и VPN или туннель до компьютера с базой).",
            -2 or 10060 =>"сервер базы данных не ответил вовремя (медленная или прерванная связь).",
            18456 => "SQL Server не принял логин или пароль из строки подключения.",
            4060 => "база данных из строки подключения не найдена или у логина нет к ней доступа.",
            _ => $"{ex.GetType().Name}: {ex.Message}",
        };
    }
}
