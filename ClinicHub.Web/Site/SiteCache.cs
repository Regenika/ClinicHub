using System.Collections.Concurrent;
using ClinicHub.Web.Data;
using Microsoft.Data.SqlClient;

namespace ClinicHub.Web.Site;

/// <summary>
/// Кэш того, что публичные страницы читают из БД (настройки, опубликованные страницы). Нужен, потому что база
/// может быть далеко и со слабой связью (демо: база на ноутбуке, сайт ходит к ней через интернет):
///   - свежие данные (моложе 30 секунд) отдаются из памяти;
///   - устаревшие отдаются сразу, а новые подгружаются в фоне — посетитель не ждёт базу;
///   - если база не ответила, остаются прежние данные, а не ошибка на странице.
/// Правка в базе видна на сайте не позже чем через ~30 секунд — так же, как у каталога (DbSiteCatalog).
/// </summary>
public sealed class SiteCache(DbConnectionStore store, TimeProvider clock, ILogger<SiteCache> logger)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private sealed record Entry(object Value, DateTimeOffset LoadedAt, string? ConnectionString);

    private readonly ConcurrentDictionary<string, Entry> _items = new();
    private readonly ConcurrentDictionary<string, byte> _refreshing = new();

    /// <summary>Значение из кэша или из базы. null из базы (например, страницы нет) не кэшируем.</summary>
    public async Task<T?> GetAsync<T>(string key, Func<Task<T?>> load) where T : class
    {
        var cs = store.ConnectionString;
        // Подключение поменяли на странице настройки — данные прежней базы не годятся.
        if (_items.TryGetValue(key, out var entry) && entry.ConnectionString == cs)
        {
            if (clock.GetUtcNow() - entry.LoadedAt >= MaxAge && _refreshing.TryAdd(key, 0))
                _ = Task.Run(() => RefreshAsync(key, load, cs));
            return (T)entry.Value;
        }

        var value = await load();
        if (value is not null)
            _items[key] = new Entry(value, clock.GetUtcNow(), cs);
        return value;
    }

    private async Task RefreshAsync<T>(string key, Func<Task<T?>> load, string? cs) where T : class
    {
        try
        {
            var value = await load();
            if (value is not null)
                _items[key] = new Entry(value, clock.GetUtcNow(), cs);
            else
                _items.TryRemove(key, out _);
        }
        catch (Exception ex) when (IsDatabaseUnavailable(ex))
        {
            // Оставляем прежние данные и не пробуем снова каждую секунду: следующая попытка — через 30 секунд.
            logger.LogWarning("Нет связи с базой, страница «{Key}» показана из кэша: {Error}", key, ex.Message);
            if (_items.TryGetValue(key, out var old))
                _items[key] = old with { LoadedAt = clock.GetUtcNow() };
        }
        finally
        {
            _refreshing.TryRemove(key, out _);
        }
    }

    /// <summary>Ошибка связи с базой (таймаут, сервер не отвечает), а не ошибка в коде.</summary>
    public static bool IsDatabaseUnavailable(Exception ex) => ex switch
    {
        SqlException => true,
        TimeoutException => true,
        InvalidOperationException { InnerException: SqlException } => true,
        Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException => true,
        _ => false,
    };
}
