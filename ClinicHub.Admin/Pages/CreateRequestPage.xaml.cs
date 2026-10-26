using System.Globalization;
using System.Text.RegularExpressions;
using ClinicHub.Admin.Services;
using ClinicHub.Core.Dtos;
using ClinicHub.Core.Entities;
using Microsoft.Maui.Controls.Shapes;

namespace ClinicHub.Admin.Pages;

/// <summary>
/// Новая заявка — редизайн по согласованному макету: узкая форма слева + контекстная панель
/// справа (Шаблоны/Контакты), без пикеров Тип/Режим отправки и без чекбокса «запланировать».
///
/// Что определяется автоматически (не вводится оператором):
/// • Тип (Personal/Mass) — по числу выбранных получателей.
/// • Режим отправки — SendMode.Now, если поля Дата/Время пустые, иначе SendMode.Custom
///   с PlannedSendAt, собранным из этих полей. Старые режимы Day/Hour/TwoWeeks (напоминания
///   «за день/час/2 недели до события») этим экраном больше не используются — они остаются
///   в системе для других сценариев (например, авто-правил), но ручная заявка их не создаёт.
///
/// Плейсхолдеры в тексте сообщения — два вида:
/// • {name} и {phone} — «умные» поля, подставляются автоматически из карточки каждого
///   конкретного получателя (у каждого получателя в итоге будет текст со своим именем/телефоном).
/// • Любые другие — например {doctor}, {cabinet}, {date}, {time} — общие для всей заявки,
///   значение для них вводится один раз в полях под строкой поиска шаблона (как было и раньше).
/// Синтаксис остаётся прежним, реальным форматом системы ({token}), а не придуманным в макете
/// «&lt;Токен&gt;» — последний в реальных шаблонах не встречается и не парсится сервером.
///
/// Вилка «мужской/женский» вариант текста — синтаксис &lt;текст-М/текст-Ж&gt; — распознаётся
/// и разрешается здесь же, в приложении, перед отправкой. У пациента в базе пока нет поля
/// «пол», поэтому вилка разрешается ОДИНАКОВО для всех получателей заявки — по переключателю
/// «Муж./Жен.» над превью. Если нужно поле пол у пациента — это отдельная доработка.
///
/// Чипы «Процедуры» из макета сюда не перенесены: в реальной системе нет сущности
/// «процедура» (ни у заявки, ни у пациента) — в макете это были decorative-данные для демо
/// плейсхолдера {{Процедура}}. Добавлять их было бы фиктивной функциональностью без реальных
/// данных за ней.
/// </summary>
public partial class CreateRequestPage : ContentPage
{
    private static readonly Regex TokenRegex = new(@"\{(\w+)\}", RegexOptions.Compiled);
    private static readonly Regex GenderForkRegex = new(@"<([^<>]*)/([^<>]*)>", RegexOptions.Compiled);
    private static readonly HashSet<string> AutoTokens = new(StringComparer.OrdinalIgnoreCase) { "name", "phone" };

    private static readonly (string Label, string Token)[] InsertableFields =
    {
        ("Имя получателя", "{name}"),
        ("Телефон получателя", "{phone}"),
        ("Дата", "{date}"),
        ("Время", "{time}"),
    };

    private readonly ApiClient _api;
    private readonly Dictionary<string, Entry> _placeholderEntries = new();

    private List<MessageTemplateDto> _templates = new();
    private List<SmsRequestOutDto> _previousRequests = new();
    private List<PatientDto> _currentFilteredPatients = new();
    private List<PatientDto> _selectedPatients = new();

    private enum RightPanelView { Templates, Contacts }
    private RightPanelView _rightPanel = RightPanelView.Templates;

    private string _previewGender = "male";
    private bool _syncingTemplateSearch;

    public CreateRequestPage(ApiClient api)
    {
        InitializeComponent();
        _api = api;

        UpdateGenderButtonStyles();
        RefreshRecipientsUi();
        ShowTemplatesPanel();

        this.Loaded += async (_, _) =>
        {
            await LoadTemplatesAsync();
            await InitContactsAsync();
        };
    }

    private async void OnHelpClicked(object? sender, EventArgs e) =>
        await DisplayAlertAsync("Справка", HelpTexts.CreateRequest, "Понятно");

    // ---------------------------------------------------------------- Шаблоны

    private async Task LoadTemplatesAsync()
    {
        try
        {
            _templates = await _api.GetAsync<List<MessageTemplateDto>>("/api/message-templates?activeOnly=true");
        }
        catch (Exception ex)
        {
            _templates = new();
            await DisplayAlertAsync("Ошибка", $"Не удалось загрузить шаблоны: {ex.Message}", "OK");
        }

        RefreshTemplatesList();
    }

    private void RefreshTemplatesList()
    {
        var q = (TemplateListSearchEntry.Text ?? "").Trim().ToLowerInvariant();
        var filtered = string.IsNullOrEmpty(q)
            ? _templates
            : _templates.Where(t => t.Name.ToLowerInvariant().Contains(q)).ToList();

        TemplatesCollectionView.ItemsSource = filtered;
    }

    private void OnTemplateFieldFocused(object? sender, FocusEventArgs e) => ShowTemplatesPanel();

    private void OnTemplateSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingTemplateSearch) return;
        _syncingTemplateSearch = true;
        TemplateListSearchEntry.Text = e.NewTextValue;
        _syncingTemplateSearch = false;
        RefreshTemplatesList();
    }

    private void OnTemplateListSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingTemplateSearch) return;
        _syncingTemplateSearch = true;
        TemplateSearchEntry.Text = e.NewTextValue;
        _syncingTemplateSearch = false;
        RefreshTemplatesList();
    }

    private void OnTemplateSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not MessageTemplateDto template)
            return;

        _syncingTemplateSearch = true;
        TemplateSearchEntry.Text = template.Name;
        TemplateListSearchEntry.Text = template.Name;
        _syncingTemplateSearch = false;

        MessageEditor.Text = template.Text;
        TemplatePreviewTitleLabel.Text = template.Name;
        TemplatePreviewTextLabel.Text = string.IsNullOrWhiteSpace(template.Text)
            ? "(пустой шаблон — впишите свой текст в поле слева)"
            : template.Text;

        RebuildPlaceholderFields(template.Text);
        UpdatePreview();
    }

    // -------------------------------------------------------- Текст сообщения

    private void OnMessageTextChanged(object? sender, TextChangedEventArgs e)
    {
        RebuildPlaceholderFields(e.NewTextValue ?? "");
        UpdatePreview();
    }

    /// <summary>Пересобирает поля общих плейсхолдеров из текущего текста сообщения (а не только
    /// из выбранного шаблона — так подхватываются и вручную вписанные/вставленные токены).
    /// {name}/{phone} сюда не попадают — они авто-подставляются на получателя при отправке.</summary>
    private void RebuildPlaceholderFields(string text)
    {
        var tokens = TokenRegex.Matches(text)
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Where(t => !AutoTokens.Contains(t))
            .ToList();

        foreach (var stale in _placeholderEntries.Keys.Except(tokens).ToList())
            _placeholderEntries.Remove(stale);

        PlaceholdersStack.Children.Clear();
        foreach (var token in tokens)
        {
            if (!_placeholderEntries.TryGetValue(token, out var entry))
            {
                entry = new Entry { Placeholder = $"Значение для {{{token}}}" };
                entry.TextChanged += (_, _) => { UpdatePreview(); UpdateSummary(); };
                _placeholderEntries[token] = entry;

                // {date}/{time} удобно сразу подставить из блока «Время отправки», если он заполнен.
                if (token.Equals("date", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ScheduleDateEntry.Text))
                    entry.Text = ScheduleDateEntry.Text;
                if (token.Equals("time", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(ScheduleTimeEntry.Text))
                    entry.Text = ScheduleTimeEntry.Text;
            }

            PlaceholdersStack.Children.Add(new Label { Text = "{" + token + "}", FontSize = 12, TextColor = Colors.Gray });
            PlaceholdersStack.Children.Add(entry);
        }
    }

    private void OnToggleFieldMenuClicked(object? sender, EventArgs e)
    {
        FieldMenuPanel.IsVisible = !FieldMenuPanel.IsVisible;
        if (FieldMenuPanel.IsVisible && FieldMenuPanel.Children.Count == 0)
        {
            foreach (var (label, token) in InsertableFields)
            {
                var btn = new Button
                {
                    Text = $"{label} {token}",
                    FontSize = 12,
                    Padding = new Thickness(10, 6),
                    BackgroundColor = Colors.White,
                    TextColor = Color.FromArgb("#1D4FBF"),
                    BorderColor = Color.FromArgb("#C7DBFF"),
                    BorderWidth = 1,
                    Margin = new Thickness(0, 0, 6, 6),
                };
                btn.Clicked += (_, _) =>
                {
                    InsertIntoMessage(token);
                    FieldMenuPanel.IsVisible = false;
                };
                FieldMenuPanel.Children.Add(btn);
            }
        }
    }

    private void OnInsertGenderForkClicked(object? sender, EventArgs e) =>
        InsertIntoMessage("<текст-М/текст-Ж>");

    private void InsertIntoMessage(string token) =>
        MessageEditor.Text = string.IsNullOrEmpty(MessageEditor.Text) ? token : MessageEditor.Text + " " + token;

    private void OnPreviewMaleClicked(object? sender, EventArgs e) { _previewGender = "male"; UpdateGenderButtonStyles(); UpdatePreview(); }
    private void OnPreviewFemaleClicked(object? sender, EventArgs e) { _previewGender = "female"; UpdateGenderButtonStyles(); UpdatePreview(); }

    private void UpdateGenderButtonStyles()
    {
        SetPillStyle(PreviewMaleButton, _previewGender == "male");
        SetPillStyle(PreviewFemaleButton, _previewGender == "female");
    }

    private static void SetPillStyle(Button b, bool selected)
    {
        b.BackgroundColor = selected ? Color.FromArgb("#2F6FED") : Colors.White;
        b.TextColor = selected ? Colors.White : Colors.Black;
        b.BorderColor = Color.FromArgb(selected ? "#2F6FED" : "#E3E0D9");
        b.BorderWidth = 1;
    }

    /// <summary>Применяет вилку М/Ж и общие (не per-recipient) плейсхолдеры. Не трогает {name}/{phone}.</summary>
    private string BuildCommonText()
    {
        var text = MessageEditor.Text ?? "";
        text = GenderForkRegex.Replace(text, m => _previewGender == "female" ? m.Groups[2].Value : m.Groups[1].Value);
        foreach (var kv in _placeholderEntries)
            text = text.Replace("{" + kv.Key + "}", kv.Value.Text ?? "");
        return text;
    }

    private static bool HasUnresolvedTokens(string commonText)
    {
        var probe = commonText.Replace("{name}", "").Replace("{phone}", "");
        return probe.Contains('{') && probe.Contains('}');
    }

    private static string BuildRecipientText(string commonText, PatientDto patient) =>
        commonText.Replace("{name}", patient.FullName).Replace("{phone}", patient.Phone);

    private void UpdatePreview()
    {
        var text = BuildCommonText();
        var demoName = _selectedPatients.Count > 0 ? _selectedPatients[0].FullName : "Иван Иванов";
        var demoPhone = _selectedPatients.Count > 0 ? _selectedPatients[0].Phone : "+7 900 000-00-00";
        text = text.Replace("{name}", demoName).Replace("{phone}", demoPhone);
        PreviewLabel.Text = string.IsNullOrWhiteSpace(text) ? "(текст пуст)" : text;
    }

    // --------------------------------------------------------- Правая панель

    private void OnShowTemplatesClicked(object? sender, EventArgs e) => ShowTemplatesPanel();
    private void OnShowContactsClicked(object? sender, EventArgs e) => ShowContactsPanel();
    private void OnOpenContactsClicked(object? sender, EventArgs e) => ShowContactsPanel();

    private void ShowTemplatesPanel()
    {
        _rightPanel = RightPanelView.Templates;
        TemplatesPanel.IsVisible = true;
        ContactsPanel.IsVisible = false;
        SetTabStyle(TemplatesTabButton, true);
        SetTabStyle(ContactsTabButton, false, _selectedPatients.Count);
    }

    private void ShowContactsPanel()
    {
        _rightPanel = RightPanelView.Contacts;
        TemplatesPanel.IsVisible = false;
        ContactsPanel.IsVisible = true;
        SetTabStyle(TemplatesTabButton, false);
        SetTabStyle(ContactsTabButton, true, _selectedPatients.Count);
        _ = SearchPatientsAsync();
    }

    private static void SetTabStyle(Button b, bool active, int? badgeCount = null)
    {
        b.BackgroundColor = active ? Colors.White : Colors.Transparent;
        b.TextColor = active ? Colors.Black : Color.FromArgb("#726F68");
        b.FontAttributes = active ? FontAttributes.Bold : FontAttributes.None;
        if (badgeCount is int n)
        {
            var baseText = b.Text?.Split(" (")[0] ?? "Контакты";
            b.Text = n > 0 ? $"{baseText} ({n})" : baseText;
        }
    }

    // ------------------------------------------------------------- Получатели

    private async Task InitContactsAsync()
    {
        try
        {
            _previousRequests = await _api.GetAsync<List<SmsRequestOutDto>>("/api/requests?take=100");
        }
        catch
        {
            // Справочник прошлых заявок опционален для фильтра — работаем без него, если недоступен.
            _previousRequests = new();
        }

        var items = new List<string> { "— не использовать —" };
        items.AddRange(_previousRequests.Select(r => $"#{r.Id} {r.Title}"));
        PreviousAudiencePicker.ItemsSource = items;
        PreviousAudiencePicker.SelectedIndex = 0;

        await SearchPatientsAsync();
    }

    private async Task SearchPatientsAsync()
    {
        try
        {
            var query = "/api/patients?take=1000";

            if (!string.IsNullOrWhiteSpace(RecipientsSearchEntry.Text))
                query += "&search=" + Uri.EscapeDataString(RecipientsSearchEntry.Text.Trim());
            if (SubscribedOnlyCheck.IsChecked)
                query += "&subscribed=true";
            if (int.TryParse(AgeMinEntry.Text, out var ageMin))
                query += "&ageMin=" + ageMin;
            if (int.TryParse(AgeMaxEntry.Text, out var ageMax))
                query += "&ageMax=" + ageMax;
            if (PreviousAudiencePicker.SelectedIndex > 0 && PreviousAudiencePicker.SelectedIndex - 1 < _previousRequests.Count)
                query += "&previousRequestId=" + _previousRequests[PreviousAudiencePicker.SelectedIndex - 1].Id;

            _currentFilteredPatients = await _api.GetAsync<List<PatientDto>>(query);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Ошибка", ex.Message, "OK");
            _currentFilteredPatients = new();
        }

        PatientsCollectionView.ItemsSource = _currentFilteredPatients;
        PatientsCollectionView.SelectedItems = _currentFilteredPatients
            .Where(p => _selectedPatients.Any(sp => sp.Id == p.Id))
            .Cast<object>()
            .ToList();
    }

    private void OnRecipientsFilterChanged(object? sender, TextChangedEventArgs e) => _ = SearchPatientsAsync();
    private void OnRecipientsFilterChanged(object? sender, CheckedChangedEventArgs e) => _ = SearchPatientsAsync();
    private void OnRecipientsFilterChanged(object? sender, EventArgs e) => _ = SearchPatientsAsync();

    private void OnPatientsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedInView = PatientsCollectionView.SelectedItems.OfType<PatientDto>().ToList();
        var viewIds = _currentFilteredPatients.Select(p => p.Id).ToHashSet();

        // Сохраняем выбор за пределами текущего фильтра (мульти-страничный выбор, как в макете),
        // заменяя только тех, кто сейчас виден в списке.
        _selectedPatients = _selectedPatients.Where(p => !viewIds.Contains(p.Id))
            .Concat(selectedInView)
            .GroupBy(p => p.Id).Select(g => g.First())
            .ToList();

        RefreshRecipientsUi();
    }

    private void OnSelectAllVisibleClicked(object? sender, EventArgs e) =>
        PatientsCollectionView.SelectedItems = _currentFilteredPatients.Cast<object>().ToList();

    private void OnClearRecipientsClicked(object? sender, EventArgs e)
    {
        PatientsCollectionView.SelectedItems = new List<object>();
        _selectedPatients = new();
        RefreshRecipientsUi();
    }

    private void RefreshRecipientsUi()
    {
        var count = _selectedPatients.Count;
        RecipientsButton.Text = count == 0
            ? "Ни одного получателя не выбрано — нажмите, чтобы выбрать"
            : count == 1
                ? $"Получатель: {_selectedPatients[0].FullName}"
                : $"Получателей выбрано: {count}";

        SetTabStyle(ContactsTabButton, _rightPanel == RightPanelView.Contacts, count);

        RecipientsChipsPreview.Children.Clear();
        SelectedChipsAllPanel.Children.Clear();
        SelectedChipsAllPanel.IsVisible = count > 0;

        foreach (var p in _selectedPatients.Take(3))
            RecipientsChipsPreview.Children.Add(BuildChip(p, removable: false));
        if (count > 3)
            RecipientsChipsPreview.Children.Add(new Label
            {
                Text = $"ещё {count - 3}",
                FontSize = 12,
                TextColor = Color.FromArgb("#726F68"),
                Margin = new Thickness(4),
                VerticalOptions = LayoutOptions.Center,
            });

        foreach (var p in _selectedPatients)
            SelectedChipsAllPanel.Children.Add(BuildChip(p, removable: true));

        UpdatePreview();
        UpdateSummary();
    }

    private View BuildChip(PatientDto p, bool removable)
    {
        var content = new HorizontalStackLayout { Spacing = 4 };
        content.Add(new Label
        {
            Text = p.FullName,
            FontSize = 12,
            TextColor = Color.FromArgb("#1D4FBF"),
            VerticalOptions = LayoutOptions.Center,
        });

        if (removable)
        {
            var removeBtn = new Button
            {
                Text = "✕",
                FontSize = 11,
                Padding = 0,
                BackgroundColor = Colors.Transparent,
                TextColor = Color.FromArgb("#726F68"),
                WidthRequest = 20,
                HeightRequest = 20,
            };
            removeBtn.Clicked += (_, _) =>
            {
                _selectedPatients = _selectedPatients.Where(x => x.Id != p.Id).ToList();
                PatientsCollectionView.SelectedItems = PatientsCollectionView.SelectedItems
                    .OfType<PatientDto>().Where(x => x.Id != p.Id).Cast<object>().ToList();
                RefreshRecipientsUi();
            };
            content.Add(removeBtn);
        }

        return new Border
        {
            StrokeShape = new RoundRectangle { CornerRadius = 999 },
            Stroke = Color.FromArgb("#C7DBFF"),
            BackgroundColor = Color.FromArgb("#EAF1FF"),
            Padding = new Thickness(10, 4),
            Margin = new Thickness(0, 0, 4, 4),
            Content = content,
        };
    }

    // ------------------------------------------------------------ Время/итог

    private void OnScheduleChanged(object? sender, TextChangedEventArgs e)
    {
        if (_placeholderEntries.TryGetValue("date", out var dateEntry) && !string.IsNullOrWhiteSpace(ScheduleDateEntry.Text))
            dateEntry.Text = ScheduleDateEntry.Text;
        if (_placeholderEntries.TryGetValue("time", out var timeEntry) && !string.IsNullOrWhiteSpace(ScheduleTimeEntry.Text))
            timeEntry.Text = ScheduleTimeEntry.Text;

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var count = _selectedPatients.Count;
        var typeLabel = count > 1 ? "массовая" : "личная";
        var hasDate = !string.IsNullOrWhiteSpace(ScheduleDateEntry.Text);
        var hasTime = !string.IsNullOrWhiteSpace(ScheduleTimeEntry.Text);
        var scheduleLabel = (hasDate || hasTime)
            ? $"запланирована на {(hasDate ? ScheduleDateEntry.Text : "(дата не указана)")}{(hasTime ? ", " + ScheduleTimeEntry.Text : "")}"
            : "уйдёт сразу после одобрения";

        SummaryLabel.Text = $"Тип: {typeLabel} ({count} получат.) · Отправка: {scheduleLabel}.";
    }

    // --------------------------------------------------------------- Отправка

    private async void OnCreateClicked(object? sender, EventArgs e)
    {
        ErrorLabel.IsVisible = false;

        if (string.IsNullOrWhiteSpace(MessageEditor.Text))
        {
            ShowError("Введите текст сообщения.");
            return;
        }

        var commonText = BuildCommonText();
        if (HasUnresolvedTokens(commonText))
        {
            ShowError("В тексте остались незаполненные поля вида {...} — заполните их значения над текстом сообщения.");
            return;
        }

        if (_selectedPatients.Count == 0)
        {
            ShowError("Выберите получателей во вкладке «Контакты» справа.");
            return;
        }

        DateTime? plannedSendAt = null;
        var hasDate = !string.IsNullOrWhiteSpace(ScheduleDateEntry.Text);
        var hasTime = !string.IsNullOrWhiteSpace(ScheduleTimeEntry.Text);

        if (hasDate || hasTime)
        {
            if (!hasDate || !hasTime ||
                !DateTime.TryParseExact(ScheduleDateEntry.Text!.Trim(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var datePart) ||
                !TimeSpan.TryParse(ScheduleTimeEntry.Text!.Trim(), out var timePart))
            {
                ShowError("Укажите дату в формате дд.мм.гггг и время в формате чч:мм — либо оставьте оба поля пустыми, чтобы отправить сразу после одобрения.");
                return;
            }

            plannedSendAt = datePart + timePart;
            if (plannedSendAt < DateTime.Now)
            {
                ShowError("Указанное время отправки — в прошлом, укажите будущее время.");
                return;
            }
        }

        var type = _selectedPatients.Count > 1 ? RequestType.Mass : RequestType.Personal;
        var mode = plannedSendAt.HasValue ? SendMode.Custom : SendMode.Now;

        var dto = new CreateSmsRequestDto
        {
            Type = type,
            SendMode = mode,
            // Пользователь вводит время по своим часам, а сервер хранит и сравнивает UTC.
            // Без перевода время уходило без пояса, сервер считал его UTC, и рассылка уходила на 3 часа позже.
            PlannedSendAt = plannedSendAt?.ToUniversalTime(),
            TextTemplate = commonText,
            Recipients = _selectedPatients.Select(p => new RecipientInputDto
            {
                PatientId = p.Id,
                Phone = p.Phone,
                MessageText = BuildRecipientText(commonText, p),
            }).ToList(),
        };

        try
        {
            var created = await _api.PostAsync<SmsRequestOutDto>("/api/requests", dto);
            await DisplayAlertAsync("Создано", $"Заявка #{created.Id} (статус: {created.Status})", "OK");
            await Navigation.PopModalAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void ShowError(string text)
    {
        ErrorLabel.Text = text;
        ErrorLabel.IsVisible = true;
    }

    private async void OnCancelClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();
}
