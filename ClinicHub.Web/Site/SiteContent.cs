using System.ComponentModel.DataAnnotations;

namespace ClinicHub.Web.Site;

/// <summary>
/// Содержимое site-content.json — только начальное наполнение пустой БД (<see cref="Data.SiteSeeder"/>):
/// сведения о клинике, страницы, каталог услуг и врачей. После первого подключения всё живёт в схеме site,
/// и правки в файле на сайт уже не влияют.
/// Значения в квадратных скобках — заглушки до сбора реального контента (PLAN, задача 4.1).
/// </summary>
public sealed class SiteContent
{
    public ClinicInfo Clinic { get; set; } = new();
    public List<ServiceItem> Services { get; set; } = [];
    public List<DoctorItem> Doctors { get; set; } = [];
    public List<PostItem> Posts { get; set; } = [];
    public List<ResultItem> Results { get; set; } = [];
    public ReviewQuote? Review { get; set; }
}

public sealed class ClinicInfo
{
    public string Name { get; set; } = "Регеника";
    public string Tagline { get; set; } = "";
    public string LegalName { get; set; } = "";
    public string License { get; set; } = "";
    public string City { get; set; } = "Санкт-Петербург";
    public string Address { get; set; } = "";
    public string Metro { get; set; } = "";
    public string Hours { get; set; } = "";
    public string Phone { get; set; } = "";

    /// <summary>Адрес сайта без слеша на конце, например https://regenika.ru. Пусто — берём из запроса.</summary>
    public string BaseUrl { get; set; } = "";

    public string TelegramUrl { get; set; } = "";
    public string MaxUrl { get; set; } = "";
    public string VkUrl { get; set; } = "";
    public RatingLink? ProDoktorov { get; set; }
    public RatingLink? YandexMaps { get; set; }
}

public sealed class RatingLink
{
    public string Url { get; set; } = "";
    public string Rating { get; set; } = "";
}

public sealed class ServiceItem
{
    /// <summary>Латинский идентификатор, идёт в адрес страницы записи.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Текст цены как есть: «от 3 000 ₽» или заглушка.</summary>
    public string Price { get; set; } = "";
    public int DurationMinutes { get; set; } = 45;
}

public sealed class DoctorItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    /// <summary>Услуги, которые ведёт врач. Пусто — все.</summary>
    public List<string> ServiceIds { get; set; } = [];
}

public sealed class PostItem
{
    [Display(Name = "Рубрика")]
    public string Tag { get; set; } = "";
    [Display(Name = "Заголовок")]
    public string Title { get; set; } = "";
    [Display(Name = "Ссылка", Description = "Пусто — заголовок без ссылки.")]
    public string Url { get; set; } = "";
}

public sealed class ResultItem
{
    [Display(Name = "Подпись", Description = "Процедура · число сеансов · интервал.")]
    public string Caption { get; set; } = "";
    [Display(Name = "Текст на месте фото")]
    public string ConsentNote { get; set; } = "";
}

public sealed class ReviewQuote
{
    [Display(Name = "Текст отзыва"), DataType(DataType.MultilineText)]
    public string Text { get; set; } = "";
    [Display(Name = "Подпись", Description = "Имя, дата, площадка.")]
    public string Signature { get; set; } = "";
}
