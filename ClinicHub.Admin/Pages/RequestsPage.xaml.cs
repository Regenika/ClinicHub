using ClinicHub.Admin.Services;
using ClinicHub.Core.Dtos;

namespace ClinicHub.Admin.Pages;

public partial class RequestsPage : ContentPage
{
    private readonly ApiClient _api;
    private readonly IServiceProvider _services;

    public RequestsPage(ApiClient api, IServiceProvider services)
    {
        InitializeComponent();
        _api = api;
        _services = services;
        this.Loaded += async (_, _) => await LoadAsync();
    }

    private async void OnHelpClicked(object? sender, EventArgs e) =>
        await DisplayAlertAsync("Справка", HelpTexts.Requests, "Понятно");

    private async Task LoadAsync()
    {
        try
        {
            RequestsView.ItemsSource = await _api.GetAsync<List<SmsRequestOutDto>>("/api/requests?take=200");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
    }

    private async void OnRequestSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not SmsRequestOutDto req)
            return;
        RequestsView.SelectedItem = null;

        var detail = await FetchDetail(req.Id);
        if (detail is null)
            return;

        var action = await DisplayActionSheetAsync(
            $"Заявка #{detail.Id} — {RuText.RequestStatus(detail.Status)}",
            "Закрыть", null,
            "Открыть",
            "Одобрить",
            "Отправить сейчас",
            "Отклонить");

        switch (action)
        {
            case "Открыть":
                await ShowDetail(detail);
                break;
            case "Одобрить":
                await PerformAsync($"/api/requests/{detail.Id}/approve", "Одобрена");
                break;
            case "Отправить сейчас":
                await PerformAsync($"/api/requests/{detail.Id}/send-now", "Запущена отправка");
                break;
            case "Отклонить":
                await PerformAsync($"/api/requests/{detail.Id}/reject", "Отклонена");
                break;
        }
    }

    private async Task PerformAsync(string url, string successText)
    {
        try
        {
            await _api.PostAsync<object>(url, new { });
            await DisplayAlertAsync("Готово", successText, "OK");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
        }
    }

    private async Task ShowDetail(SmsRequestOutDto d)
    {
        string message = $"Статус: {RuText.RequestStatus(d.Status)}\nЦель: {RuText.Purpose(d.Purpose)}\n" +
                         $"Тип: {RuText.RequestType(d.Type)}\nОтправка: {RuText.SendMode(d.SendMode)}\n" +
                         $"Планируемая отправка: {d.PlannedSendAt?.ToLocalTime():dd.MM.yyyy HH:mm}\n" +
                         $"Получателей: {d.RecipientCount} (отправлено {d.SentCount}, ошибок {d.FailedCount}, пропущено {d.SkippedCount})";

        if (d.Recipients.Count > 0)
        {
            var recs = string.Join("\n",
                d.Recipients.Select(r => $"  • {r.Phone} — {RuText.RecipientStatus(r.Status)}"
                    + (string.IsNullOrEmpty(r.SkipReason) ? "" : $" ({r.SkipReason})")));
            message += $"\n\nПолучатели:\n{recs}";
        }

        await DisplayAlertAsync($"Заявка #{d.Id}", message, "OK");
    }

    private async Task<SmsRequestOutDto?> FetchDetail(int id)
    {
        try
        {
            return await _api.GetAsync<SmsRequestOutDto>($"/api/requests/{id}");
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
            return null;
        }
    }

    private async void OnCreateClicked(object? sender, EventArgs e)
    {
        await Navigation.PushModalAsync(_services.GetRequiredService<CreateRequestPage>());
    }
}