using ClinicHub.Web.Site;

namespace ClinicHub.Web.Blocks;

// Настройки блоков. Хранятся JSON в site.Blocks.Data. Значения по умолчанию — как на сайте сейчас,
// поэтому поле, которого нет в JSON (блок сохранён старой версией), не ломает страницу.
// Anchor — id раздела для ссылок меню («/#services»).

/// <summary>Обложка: заголовок, подзаголовок, две кнопки, факты о клинике из настроек.</summary>
public sealed record HeroBlock
{
    /// <summary>Строка над заголовком. Пусто — «слоган · город» из настроек.</summary>
    public string Eyebrow { get; init; } = "";
    public string Title { get; init; } = "Косметология, где сначала смотрит врач";
    public string Lead { get; init; } = "Процедуры назначает врач-косметолог после осмотра и консультации. Перед визитом напомним о нём в удобном мессенджере или по SMS.";
    public string PrimaryText { get; init; } = "Записаться на консультацию";
    /// <summary>Услуга из каталога, на которую ведёт главная кнопка. Пусто — на первый шаг записи.</summary>
    public string PrimaryServiceId { get; init; } = "consultation";
    /// <summary>Вторая кнопка ведёт в Telegram клиники (ссылка из настроек). Пусто — кнопки нет.</summary>
    public string TelegramText { get; init; } = "Задать вопрос в Telegram";
    public bool ShowFacts { get; init; } = true;
    public string PhotoNote { get; init; } = "[ФОТО: кабинет или врач на приёме]";
}

/// <summary>Быстрая запись: услуга и врач из каталога, переход сразу к выбору времени.</summary>
public sealed record QuickBookingBlock
{
    public string ButtonText { get; init; } = "Показать свободное время";
}

/// <summary>Карточки услуг. Названия, описания и цены — из каталога, блок хранит только выбор.</summary>
public sealed record ServicesBlock
{
    public string Anchor { get; init; } = "services";
    public string Title { get; init; } = "Услуги и цены";
    /// <summary>Какие услуги показать и в каком порядке. Пусто — все из каталога.</summary>
    public List<string> ServiceIds { get; init; } = [];
    public string Note { get; init; } = "Имеются противопоказания. Необходима консультация специалиста.";
}

public sealed record DoctorsBlock
{
    public string Anchor { get; init; } = "doctors";
    public string Title { get; init; } = "Врачи";
    /// <summary>Каких врачей показать и в каком порядке. Пусто — всех из каталога.</summary>
    public List<string> DoctorIds { get; init; } = [];
}

public sealed record ResultsBlock
{
    public string Anchor { get; init; } = "results";
    public string Title { get; init; } = "До и после";
    public string Intro { get; init; } = "Публикуем фотографии только с письменного согласия пациента. На каждой карточке указаны процедура, число сеансов и время между снимками.";
    public List<ResultItem> Items { get; init; } = [];
}

/// <summary>Отзывы: ссылки на площадки (из настроек) и одна цитата.</summary>
public sealed record ReviewsBlock
{
    public string Anchor { get; init; } = "reviews";
    public string Title { get; init; } = "Отзывы";
    public string Intro { get; init; } = "Собираем отзывы на ПроДокторов и Яндекс Картах. Там их проверяют модераторы, поэтому на сайте мы показываем ссылки, а не пересказ.";
    public ReviewQuote? Quote { get; init; }
}

/// <summary>Подписка на бота клиники. Ссылки на Telegram, MAX и ВКонтакте — из настроек.</summary>
public sealed record ChannelsBlock
{
    public string Title { get; init; } = "Напоминания и новости там, где вам удобно";
    public string Text { get; init; } = "Подключите бота клиники. Через него можно подтвердить или перенести визит одной кнопкой. Акции присылаем, только если вы сами на них подпишетесь.";
}

public sealed record PostsBlock
{
    public string Anchor { get; init; } = "blog";
    public string Title { get; init; } = "Статьи врачей";
    public List<PostItem> Items { get; init; } = [];
}
