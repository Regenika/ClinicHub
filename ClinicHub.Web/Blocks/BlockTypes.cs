using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ClinicHub.Web.Components.Blocks;

namespace ClinicHub.Web.Blocks;

/// <summary>
/// Тип блока: ключ в БД, название для редактора, класс настроек и компонент отрисовки (ADR-0007 §1).
/// Новый тип блока: класс настроек в BlockSettings.cs, компонент в Components/Blocks, строка в <see cref="BlockTypes.All"/>.
/// Форма редактора появится вместе с редактором страниц (PLAN 4.8).
/// </summary>
public sealed record BlockType(string Key, string Title, Type SettingsType, Type ComponentType)
{
    public object Deserialize(string json) =>
        JsonSerializer.Deserialize(json, SettingsType, BlockTypes.Json)
        ?? Activator.CreateInstance(SettingsType)!;
}

public static class BlockTypes
{
    /// <summary>JSON настроек блока: имена в camelCase, кириллица как есть (так JSON в БД читается глазами).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static readonly IReadOnlyDictionary<string, BlockType> All = new BlockType[]
    {
        new("hero", "Обложка", typeof(HeroBlock), typeof(HeroView)),
        new("quick-booking", "Быстрая запись", typeof(QuickBookingBlock), typeof(QuickBookingView)),
        new("services", "Услуги", typeof(ServicesBlock), typeof(ServicesView)),
        new("doctors", "Специалисты", typeof(DoctorsBlock), typeof(DoctorsView)),
        new("results", "До и после", typeof(ResultsBlock), typeof(ResultsView)),
        new("reviews", "Отзывы", typeof(ReviewsBlock), typeof(ReviewsView)),
        new("channels", "Каналы", typeof(ChannelsBlock), typeof(ChannelsView)),
        new("posts", "Статьи", typeof(PostsBlock), typeof(PostsView)),
        new("text", "Текст", typeof(TextBlock), typeof(TextView)),
    }.ToDictionary(t => t.Key);

    public static string KeyOf(Type settingsType) => All.Values.Single(t => t.SettingsType == settingsType).Key;

    public static string Serialize(object settings) => JsonSerializer.Serialize(settings, settings.GetType(), Json);
}
