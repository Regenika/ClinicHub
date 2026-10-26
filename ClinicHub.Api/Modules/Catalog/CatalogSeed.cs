using ClinicHub.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHub.Api.Modules.Catalog;

/// <summary>
/// Стартовое наполнение каталога — те же услуги и врачи, что сейчас в ClinicHub.Web/site-content.json,
/// чтобы было с чем работать. Выполняется только на пустом каталоге, дальше каталог правится через API.
/// Цен и части описаний на сайте пока нет (заглушки в скобках) — в каталог заглушки не переносим: PriceFrom = null, описание пустое. Врачей-заглушек «[ИМЯ ВРАЧА N]»
/// не переносим: в каталоге будут только реальные записи, остальных добавят через админку.
/// </summary>
public static class CatalogSeed
{
    public static async Task SeedAsync(ClinicHubDbContext db, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Services.AnyAsync(ct))
            return;

        var services = new[]
        {
            Svc("consultation", "Консультация врача-косметолога", "Осмотр, диагностика кожи и план процедур. С неё начинается любой курс.", 45, 10),
            Svc("facial-care", "Уход за лицом и телом", "Уходовые программы для кожи лица и тела под её тип и состояние.", 60, 20),
            Svc("hardware", "Аппаратная косметология", "SMAS-лифтинг, игольчатый RF, фотоомоложение.", 60, 30),
            Svc("injections", "Инъекционная косметология", "Ботулинотерапия и другие инъекционные методики. Только после консультации и при отсутствии противопоказаний.", 45, 40),
            Svc("prp", "PRP и клеточные технологии", "Регенеративные методики на основе собственной плазмы, в том числе PRP CORTEXIL.", 45, 50),
            Svc("courses", "Курсы и абонементы", "", 45, 60),
        };
        db.Services.AddRange(services);

        var head = new Doctor
        {
            Id = "lastochkina-shankina",
            Name = "Ласточкина-Шанкина Елена Валентиновна",
            Role = "Руководитель клиники · косметолог, дерматолог, пластический хирург, физиотерапевт",
            SortOrder = 10,
        };
        // На сайте у неё пустой список услуг, что значит «ведёт все». В каталоге связи явные.
        head.Services.AddRange(services.Select(s => new DoctorService { DoctorId = head.Id, ServiceId = s.Id }));
        db.Doctors.Add(head);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Каталог заполнен стартовыми данными: услуг {Services}, врачей {Doctors}.", services.Length, 1);
    }

    private static Service Svc(string id, string name, string description, int minutes, int order) => new()
    {
        Id = id,
        Name = name,
        Description = description,
        DurationMinutes = minutes,
        SortOrder = order,
    };
}
