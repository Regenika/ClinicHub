using System.ComponentModel.DataAnnotations;
using ClinicHub.Web.Site;

namespace ClinicHub.Web.Blocks;

// Настройки блоков. Хранятся JSON в site.Blocks.Data. Значения по умолчанию — как на сайте сейчас,
// поэтому поле, которого нет в JSON (блок сохранён старой версией), не ломает страницу.
// Anchor — id раздела для ссылок меню («/#services»).
// [Display] — подпись поля в редакторе страниц, [DataType(MultilineText)] — многострочное поле.
// Форма редактора строится по этим атрибутам (Components/Admin/BlockFields.razor), отдельную форму на тип писать не нужно.

/// <summary>Обложка: заголовок, подзаголовок, две кнопки, факты о клинике из настроек.</summary>
public sealed record HeroBlock
{
    [Display(Name = "Строка над заголовком", Description = "Пусто — «слоган · город» из настроек сайта.")]
    public string Eyebrow { get; init; } = "";
    [Display(Name = "Заголовок")]
    public string Title { get; init; } = "Косметология, где сначала смотрит врач";
    [Display(Name = "Подзаголовок"), DataType(DataType.MultilineText)]
    public string Lead { get; init; } = "Процедуры назначает врач-косметолог после осмотра и консультации. Перед визитом напомним о нём в удобном мессенджере или по SMS.";
    [Display(Name = "Текст главной кнопки")]
    public string PrimaryText { get; init; } = "Записаться на консультацию";
    /// <summary>Услуга из каталога, на которую ведёт главная кнопка. Пусто — на первый шаг записи.</summary>
    [Display(Name = "Главная кнопка ведёт на запись к услуге", Description = "Пусто — на выбор услуги.")]
    public string PrimaryServiceId { get; init; } = "consultation";
    /// <summary>Вторая кнопка ведёт в Telegram клиники (ссылка из настроек). Пусто — кнопки нет.</summary>
    [Display(Name = "Текст кнопки Telegram", Description = "Пусто — кнопки нет. Ссылка — из настроек сайта.")]
    public string TelegramText { get; init; } = "Задать вопрос в Telegram";
    [Display(Name = "Показывать метро, часы работы и лицензию")]
    public bool ShowFacts { get; init; } = true;
    [Display(Name = "Фото", Description = "Из медиатеки. Пока не выбрано — на сайте место для фото с подписью ниже."), MediaImage]
    public int? PhotoId { get; init; }
    [Display(Name = "Подпись места для фото")]
    public string PhotoNote { get; init; } = "[ФОТО: кабинет или врач на приёме]";
}

/// <summary>Быстрая запись: услуга и специалист из каталога, переход сразу к выбору времени.</summary>
public sealed record QuickBookingBlock
{
    [Display(Name = "Текст кнопки")]
    public string ButtonText { get; init; } = "Показать свободное время";
}

/// <summary>Карточки услуг. Названия, описания и цены — из каталога, блок хранит только выбор.</summary>
public sealed record ServicesBlock
{
    [Display(Name = "Якорь для меню", Description = "Латиницей: ссылка «/#services» ведёт к этому разделу.")]
    public string Anchor { get; init; } = "services";
    [Display(Name = "Заголовок")]
    public string Title { get; init; } = "Услуги и цены";
    /// <summary>Какие услуги показать и в каком порядке. Пусто — все из каталога.</summary>
    [Display(Name = "Какие услуги показать", Description = "Ни одной — все из каталога в его порядке. Цены — из каталога.")]
    public List<string> ServiceIds { get; init; } = [];
    [Display(Name = "Примечание под карточками")]
    public string Note { get; init; } = "Имеются противопоказания. Необходима консультация специалиста.";
}

public sealed record DoctorsBlock
{
    [Display(Name = "Якорь для меню")]
    public string Anchor { get; init; } = "doctors";
    [Display(Name = "Заголовок")]
    public string Title { get; init; } = "Специалисты";
    /// <summary>Каких специалистов показать и в каком порядке. Пусто — всех из каталога.</summary>
    [Display(Name = "Каких специалистов показать", Description = "Ни одного — всех из каталога.")]
    public List<string> DoctorIds { get; init; } = [];
}

public sealed record ResultsBlock
{
    [Display(Name = "Якорь для меню")]
    public string Anchor { get; init; } = "results";
    [Display(Name = "Заголовок")]
    public string Title { get; init; } = "До и после";
    [Display(Name = "Вступление"), DataType(DataType.MultilineText)]
    public string Intro { get; init; } = "Публикуем фотографии только с письменного согласия пациента. На каждой карточке указаны процедура, число сеансов и время между снимками.";
    [Display(Name = "Карточки")]
    public List<ResultItem> Items { get; init; } = [];
}

/// <summary>Отзывы: ссылки на площадки (из настроек) и одна цитата.</summary>
public sealed record ReviewsBlock
{
    [Display(Name = "Якорь для меню")]
    public string Anchor { get; init; } = "reviews";
    [Display(Name = "Заголовок")]
    public string Title { get; init; } = "Отзывы";
    [Display(Name = "Вступление"), DataType(DataType.MultilineText)]
    public string Intro { get; init; } = "Собираем отзывы на ПроДокторов и Яндекс Картах. Там их проверяют модераторы, поэтому на сайте мы показываем ссылки, а не пересказ.";
    [Display(Name = "Цитата", Description = "Только с согласия автора и без медицинских подробностей.")]
    public ReviewQuote? Quote { get; init; }
}

/// <summary>Подписка на бота клиники. Ссылки на Telegram, MAX и ВКонтакте — из настроек.</summary>
public sealed record ChannelsBlock
{
    [Display(Name = "Заголовок")]
    public string Title { get; init; } = "Напоминания и новости там, где вам удобно";
    [Display(Name = "Текст"), DataType(DataType.MultilineText)]
    public string Text { get; init; } = "Подключите бота клиники. Через него можно подтвердить или перенести визит одной кнопкой. Акции присылаем, только если вы сами на них подпишетесь.";
}

public sealed record PostsBlock
{
    [Display(Name = "Якорь для меню")]
    public string Anchor { get; init; } = "blog";
    [Display(Name = "Заголовок")]
    public string Title { get; init; } = "Статьи врачей";
    [Display(Name = "Статьи")]
    public List<PostItem> Items { get; init; } = [];
}

/// <summary>
/// Текст: заголовок и абзацы. Без HTML — абзацы разделяются пустой строкой, поэтому вставить скрипт
/// или сломать вёрстку нельзя (ADR-0007 §1). Ограниченная разметка (жирный, списки, ссылки) — позже, с санитайзером.
/// </summary>
public sealed record TextBlock
{
    [Display(Name = "Якорь для меню", Description = "Необязательно.")]
    public string Anchor { get; init; } = "";
    [Display(Name = "Заголовок")]
    public string Title { get; init; } = "";
    [Display(Name = "Текст", Description = "Абзацы разделяйте пустой строкой."), DataType(DataType.MultilineText)]
    public string Text { get; init; } = "";
    [Display(Name = "Картинка", Description = "Необязательно. Показывается над текстом."), MediaImage]
    public int? ImageId { get; init; }
}
