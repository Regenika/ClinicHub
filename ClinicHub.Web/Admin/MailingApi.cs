using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClinicHub.Core.Dtos;
using Microsoft.AspNetCore.Components.Authorization;

namespace ClinicHub.Web.Admin;

/// <summary>
/// Панель рассылок на сайте ходит в сервер рассылок (ClinicHub.Api) на этом же компьютере (ADR-0012).
/// Вход — ключ панели из файла, который создаёт сервер; сотрудника проверяет сайт, серверу передаётся его логин.
/// </summary>
public sealed class MailingApiOptions
{
    /// <summary>Адрес сервера рассылок. Api:BaseUrl, по умолчанию http://localhost:5112.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5112";
    /// <summary>Файл ключа панели. Panel:KeyFile, по умолчанию %ProgramData%\ClinicHub\panel.key (его пишет сервер).</summary>
    public string? KeyFile { get; set; }

    public string KeyPath => KeyFile is { Length: > 0 }
        ? KeyFile
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClinicHub", "panel.key");
}

/// <summary>Понятная ошибка для страницы: что не так и что делать.</summary>
public sealed class MailingApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

public sealed class MailingApi(HttpClient http, MailingApiOptions options, AuthenticationStateProvider auth)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    // ── заявки и рассылки ─────────────────────────────────────────────────────
    public Task<List<SmsRequestOutDto>> RequestsAsync(int take = 100) => GetAsync<List<SmsRequestOutDto>>($"api/requests?take={take}");
    public Task<SmsRequestOutDto> RequestAsync(int id) => GetAsync<SmsRequestOutDto>($"api/requests/{id}");
    public Task<SmsRequestOutDto> CreateRequestAsync(CreateSmsRequestDto dto) => PostAsync<SmsRequestOutDto>("api/requests", dto);
    public Task<RequestEstimateDto> EstimateAsync(CreateSmsRequestDto dto) => PostAsync<RequestEstimateDto>("api/requests/estimate", dto);
    public Task PauseAsync(int id) => PostAsync<JsonElement>($"api/requests/{id}/pause", null);
    public Task ResumeAsync(int id) => PostAsync<JsonElement>($"api/requests/{id}/resume", null);
    public Task CancelAsync(int id) => PostAsync<JsonElement>($"api/requests/{id}/cancel", null);
    public Task SendNowAsync(int id) => PostAsync<JsonElement>($"api/requests/{id}/send-now", null);
    public Task<List<RecipientLogDto>> LogAsync(string? status = null, int? patientId = null, int? requestId = null, int take = 200) =>
        GetAsync<List<RecipientLogDto>>($"api/requests/recipients?take={take}"
            + (status is null ? "" : $"&status={status}") + (patientId is int p ? $"&patientId={p}" : "")
            + (requestId is int r ? $"&requestId={r}" : ""));

    // ── пациенты и согласия ───────────────────────────────────────────────────
    public Task<List<PatientDto>> PatientsAsync(string? search, int take = 50) =>
        GetAsync<List<PatientDto>>($"api/patients?take={take}" + (string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search.Trim())));
    public Task<PatientDto> PatientAsync(int id) => GetAsync<PatientDto>($"api/patients/{id}");
    public Task<PatientDto> CreatePatientAsync(PatientInputDto dto) => PostAsync<PatientDto>("api/patients", dto);
    public Task<PatientImportResultDto> ImportPatientsAsync(PatientImportRequestDto dto) => PostAsync<PatientImportResultDto>("api/patients/import", dto);
    public Task<AudienceDto> AudienceAsync(string kind, int? months = null, string? serviceId = null, int? month = null) =>
        GetAsync<AudienceDto>($"api/patients/audience?kind={kind}"
            + (months is int m ? $"&months={m}" : "") + (serviceId is null ? "" : "&serviceId=" + Uri.EscapeDataString(serviceId))
            + (month is int mo ? $"&month={mo}" : ""));
    public Task<PatientConsentsDto> ConsentsAsync(int patientId) => GetAsync<PatientConsentsDto>($"api/patients/{patientId}/consents");
    public Task AddConsentAsync(int patientId, ConsentInputDto dto) => PostAsync<JsonElement>($"api/patients/{patientId}/consents", dto);

    // ── шаблоны, телефоны, автоматические ─────────────────────────────────────
    public Task<List<MessageTemplateDto>> TemplatesAsync() => GetAsync<List<MessageTemplateDto>>("api/message-templates");
    public Task<MessageTemplateDto> CreateTemplateAsync(MessageTemplateInputDto dto) => PostAsync<MessageTemplateDto>("api/message-templates", dto);
    public Task UpdateTemplateAsync(int id, MessageTemplateInputDto dto) => SendAsync<JsonElement>(HttpMethod.Put, $"api/message-templates/{id}", dto);
    public Task DeleteTemplateAsync(int id) => SendAsync<JsonElement>(HttpMethod.Delete, $"api/message-templates/{id}", null);

    public Task<List<PhoneDto>> PhonesAsync() => GetAsync<List<PhoneDto>>("api/phones");
    public Task SetPhoneActiveAsync(int id, bool active) => PostAsync<JsonElement>($"api/phones/{id}/{(active ? "activate" : "deactivate")}", null);
    public Task SetBalanceAsync(int simId, int balance) => PostAsync<JsonElement>($"api/phones/sim/{simId}/balance", new SetBalanceDto { NewBalance = balance, Reason = "введено в панели" });
    public Task<DashboardSummaryDto> DashboardAsync() => GetAsync<DashboardSummaryDto>("api/dashboard/summary");

    public Task<List<ScenarioDto>> ScenariosAsync() => GetAsync<List<ScenarioDto>>("api/scenarios");
    public Task<ScenarioDto> CreateScenarioAsync(ScenarioInputDto dto) => PostAsync<ScenarioDto>("api/scenarios", dto);
    public Task UpdateScenarioAsync(int id, ScenarioInputDto dto) => SendAsync<JsonElement>(HttpMethod.Put, $"api/scenarios/{id}", dto);
    public Task<List<AutoRuleDto>> AutoRulesAsync() => GetAsync<List<AutoRuleDto>>("api/auto-rules");
    public Task<AutoRuleDto> CreateAutoRuleAsync(AutoRuleInputDto dto) => PostAsync<AutoRuleDto>("api/auto-rules", dto);
    public Task UpdateAutoRuleAsync(int id, AutoRuleInputDto dto) => SendAsync<JsonElement>(HttpMethod.Put, $"api/auto-rules/{id}", dto);
    public Task<DikidiHistoryDto> DikidiHistoryAsync(DateOnly from, DateOnly to) =>
        PostAsync<DikidiHistoryDto>($"api/integrations/dikidi/history?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", null);
    public Task<List<CatalogServiceRef>> ServicesAsync() => GetAsync<List<CatalogServiceRef>>("api/catalog/admin/services");

    // ── транспорт ─────────────────────────────────────────────────────────────
    private Task<T> GetAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url, null);
    private Task<T> PostAsync<T>(string url, object? body) => SendAsync<T>(HttpMethod.Post, url, body);

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body)
    {
        var key = ReadKey();
        using var request = new HttpRequestMessage(method, new Uri(new Uri(options.BaseUrl.TrimEnd('/') + "/"), url));
        request.Headers.Add("X-Panel-Key", key);
        var user = (await auth.GetAuthenticationStateAsync()).User;
        request.Headers.Add("X-Panel-User", Uri.EscapeDataString(user.Identity?.Name ?? "сайт"));
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new MailingApiException(
                "Сервер рассылок не отвечает. Проверьте, что служба «ClinicHub API» запущена (Пуск → «Службы»).");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (typeof(T) == typeof(JsonElement) || response.StatusCode == HttpStatusCode.NoContent)
                    return default!;
                return (await response.Content.ReadFromJsonAsync<T>(Json))!;
            }

            var text = (await response.Content.ReadAsStringAsync()).Trim();
            if (text.StartsWith('{'))
            {
                // ProblemDetails: берём detail («Ошибка Dikidi: …»), а не весь JSON.
                try { text = JsonDocument.Parse(text).RootElement.TryGetProperty("detail", out var d) ? d.GetString() ?? "" : ""; }
                catch (JsonException) { text = ""; }
            }
            text = text.Trim('"');
            throw new MailingApiException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Сервер рассылок не принял ключ панели. Обычно помогает перезапустить службу «ClinicHub Web» после установки сервера.",
                HttpStatusCode.NotFound => "Не найдено — возможно, запись уже удалили.",
                HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable when text.Length == 0 =>
                    "Сервер рассылок сейчас без связи с базой данных. Он подключится сам; попробуйте через минуту.",
                _ => text.Length > 0 && text.Length < 400 ? text : $"Сервер рассылок ответил ошибкой {(int)response.StatusCode}.",
            }, response.StatusCode);
        }
    }

    private string ReadKey()
    {
        try
        {
            var key = File.ReadAllText(options.KeyPath).Trim();
            if (key.Length > 0)
                return key;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        throw new MailingApiException(
            $"Нет ключа панели ({options.KeyPath}). Его создаёт сервер рассылок при запуске — проверьте, что он установлен и запущен на этом компьютере.");
    }
}

public sealed class AudienceDto
{
    public int Count { get; set; }
    public List<int> Ids { get; set; } = new();
}

public sealed class CatalogServiceRef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

// Сценарии и согласия описаны в сервере, а не в общей библиотеке — здесь их копия для чтения JSON.
public sealed record ScenarioStepDto(
    int? Id, int Order, int OffsetDays, string SendTime, string Text,
    bool CancelIfFutureVisit = false, bool CancelIfVisitAfter = false, string? CancelIfServiceAfterId = null);

public sealed record ScenarioDto(
    int Id, string Name, bool IsActive, string Purpose, string Trigger, int? NoVisitDays,
    IReadOnlyList<string> TriggerServiceIds, IReadOnlyList<ScenarioStepDto> Steps, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record ScenarioInputDto(
    string Name, string Purpose, string Trigger, IReadOnlyList<ScenarioStepDto>? Steps,
    IReadOnlyList<string>? TriggerServiceIds = null, int? NoVisitDays = null, bool IsActive = true);

public sealed record ConsentStateDto(string Purpose, string Channel, string State, DateTime? At, long? RecordId, bool CanSend, string Reason);
public sealed record ConsentRecordDto(long Id, string Purpose, string Channel, bool Granted, string Source,
    string? EvidenceRef, DateTime At, DateTime RecordedAt, int? ById, string? ByLogin, string? Comment);
public sealed record PatientConsentsDto(int PatientId, bool IsSubscribed, IReadOnlyList<ConsentStateDto> Current, IReadOnlyList<ConsentRecordDto> Journal);
public sealed record ConsentInputDto(string Purpose, string Channel, bool Granted, string Source,
    string? EvidenceRef = null, string? Comment = null, DateTime? At = null);

/// <summary>Итог загрузки клиентов из прошлых записей журнала Dikidi (сервер: DikidiHistoryResult).</summary>
public sealed record DikidiHistoryDto(int Records, int PatientsCreated, int LastVisitUpdated, int SkippedNoPhone);
