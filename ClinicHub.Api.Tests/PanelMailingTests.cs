using ClinicHub.Api.Endpoints;
using ClinicHub.Api.Modules.Shared;

namespace ClinicHub.Api.Tests;

/// <summary>Расчёт окончания рассылки и подстановка имени для веб-панели (ADR-0012). Пациенты вымышленные.</summary>
public sealed class PanelMailingTests
{
    private static readonly TimeSpan From = new(9, 0, 0);
    private static readonly TimeSpan To = new(20, 0, 0);

    private static DateTime Msk(int day, int hour, int minute) =>
        MoscowTime.ToUtc(new DateOnly(2026, 11, day), new TimeOnly(hour, minute));

    [Fact]
    public void Окончание_ВПределахДня()
    {
        // 15:00, 120 сообщений по 2 в минуту → 16:00 того же дня.
        var end = RequestsEndpoints.EstimateFinish(Msk(17, 15, 0), 120, 2, 600, From, To);
        Assert.Equal(Msk(17, 16, 0), end);
    }

    [Fact]
    public void Окончание_ПереносНаУтро()
    {
        // 19:30, 1 в минуту, 60 сообщений: 30 сегодня до 20:00, остальные 30 — с 9:00 завтра → 9:30.
        var end = RequestsEndpoints.EstimateFinish(Msk(17, 19, 30), 60, 1, 300, From, To);
        Assert.Equal(Msk(18, 9, 30), end);
    }

    [Fact]
    public void Окончание_НочьюНачинаемУтром()
    {
        var end = RequestsEndpoints.EstimateFinish(Msk(17, 23, 0), 10, 1, 300, From, To);
        Assert.Equal(Msk(18, 9, 10), end);
    }

    [Fact]
    public void Окончание_СуточныйЛимит()
    {
        // Лимит 100 в сутки: 250 сообщений с 9:00 → третий день, ещё 50 минут.
        var end = RequestsEndpoints.EstimateFinish(Msk(17, 9, 0), 250, 5, 100, From, To);
        Assert.Equal(Msk(19, 9, 10), end);
    }

    [Theory]
    [InlineData("{Имя}, ваши анализы готовы.", "Тестова Анна", "Тестова Анна, ваши анализы готовы.")]
    [InlineData("{name}, здравствуйте!", "Тестова Анна", "Тестова Анна, здравствуйте!")]
    [InlineData("{Имя}, ваши анализы готовы.", null, "ваши анализы готовы.")]
    [InlineData("Клиника закрыта 4 ноября.", "Тестова Анна", "Клиника закрыта 4 ноября.")]
    public void ПодстановкаИмени(string text, string? name, string expected) =>
        Assert.Equal(expected, RequestsEndpoints.Personalize(text, name));
}
