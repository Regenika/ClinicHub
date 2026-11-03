using ClinicHub.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Web.Site;

/// <summary>
/// Каталог для сайта и записи: услуги с ценами и врачи (ADR-0007 §2). Блоки страниц хранят только id,
/// а названия и цены берутся отсюда, поэтому цена правится в одном месте.
/// </summary>
public interface ISiteCatalog
{
    IReadOnlyList<ServiceItem> Services { get; }
    IReadOnlyList<DoctorItem> Doctors { get; }
}

/// <summary>
/// Временный каталог из таблиц site.Catalog* (см. Data/CatalogEntities.cs). Каталог принадлежит серверу
/// (модуль Catalog в ClinicHub.Api, ADR-0008 §2, Issue #13); когда там появится API, здесь будет его клиент.
/// Каталог маленький и нужен на каждой странице, поэтому держим его в памяти и перечитываем раз в 30 секунд:
/// цена, исправленная в базе, появляется на всех сайтах, подключённых к ней, не позже чем через полминуты.
/// </summary>
public sealed class DbSiteCatalog(IDbContextFactory<SiteDbContext> dbFactory, DbConnectionStore store, TimeProvider clock,
    ILogger<DbSiteCatalog> logger) : ISiteCatalog
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private sealed record Snapshot(IReadOnlyList<ServiceItem> Services, IReadOnlyList<DoctorItem> Doctors, DateTimeOffset LoadedAt);

    private static readonly Snapshot Empty = new([], [], DateTimeOffset.MinValue);

    private volatile Snapshot _snapshot = Empty;
    private int _refreshing;

    /// <summary>После правки в админке: следующее обращение читает каталог из базы.</summary>
    public void Invalidate() => _snapshot = _snapshot with { LoadedAt = DateTimeOffset.MinValue };

    public IReadOnlyList<ServiceItem> Services => Current().Services;
    public IReadOnlyList<DoctorItem> Doctors => Current().Doctors;

    private Snapshot Current()
    {
        var s = _snapshot;
        if (store.State != DbState.Ready || clock.GetUtcNow() - s.LoadedAt < MaxAge)
            return s;

        if (s.LoadedAt == DateTimeOffset.MinValue)
        {
            // Первое обращение после запуска (без каталога страница пустая) или правка в админке (редактор
            // сразу хочет увидеть результат) — ждём загрузку.
            Load();
            return _snapshot;
        }

        // Данные устарели: отдаём прежние, а свежие подгружаем в фоне одним запросом.
        if (Interlocked.Exchange(ref _refreshing, 1) == 0)
            _ = Task.Run(() => { try { Load(); } finally { Volatile.Write(ref _refreshing, 0); } });
        return s;
    }

    private void Load()
    {
        try
        {
            using var db = dbFactory.CreateDbContext();
            var services = db.CatalogServices.AsNoTracking()
                .Where(x => x.IsVisible)
                .OrderBy(x => x.Order).ThenBy(x => x.Id)
                .Select(x => new ServiceItem { Id = x.Key, Name = x.Name, Text = x.Text, Price = x.Price, DurationMinutes = x.DurationMinutes })
                .ToList();
            var doctors = db.CatalogDoctors.AsNoTracking()
                .Where(x => x.IsVisible)
                .OrderBy(x => x.Order).ThenBy(x => x.Id)
                .Select(x => new DoctorItem
                {
                    Id = x.Key,
                    Name = x.Name,
                    Role = x.Role,
                    PhotoId = x.PhotoId,
                    ServiceIds = x.Services.Select(ds => ds.Service.Key).ToList(),
                })
                .ToList();
            _snapshot = new Snapshot(services, doctors, clock.GetUtcNow());
        }
        catch (Exception ex) when (ex is Microsoft.Data.SqlClient.SqlException or InvalidOperationException)
        {
            // База на время недоступна — показываем то, что уже загружено, и пробуем снова через 30 секунд.
            logger.LogWarning(ex, "Не удалось обновить каталог из БД, показываем загруженный ранее");
            _snapshot = _snapshot with { LoadedAt = clock.GetUtcNow() };
        }
    }
}

public static class SiteCatalogExtensions
{
    /// <summary>Врач ведёт услугу. Пустой список услуг у врача — ведёт все.</summary>
    public static bool Treats(this DoctorItem doctor, string serviceId) =>
        doctor.ServiceIds.Count == 0 || doctor.ServiceIds.Contains(serviceId);

    /// <summary>Выбранные элементы в заданном порядке; пустой выбор — все. Неизвестные id пропускаем (удалили из каталога).</summary>
    public static IEnumerable<T> Pick<T>(this IReadOnlyList<T> all, IReadOnlyList<string> ids, Func<T, string> id) =>
        ids.Count == 0 ? all : ids.Select(x => all.FirstOrDefault(item => id(item) == x)).OfType<T>();
}
