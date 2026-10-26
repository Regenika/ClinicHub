using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClinicHub.Api.Modules.Integrations.Dikidi;

/// <summary>Источник журнала. Интерфейс — чтобы синхронизацию можно было проверить без Dikidi.</summary>
public interface IDikidiClient
{
    /// <summary>Записи за период по датам журнала (включительно с обеих сторон).</summary>
    Task<DikidiJournal> GetJournalAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>
/// Неофициальный доступ к журналу Dikidi так, как это делает веб-кабинет (протокол — по обёртке
/// Igor20-1/dikidi-api-client, Issue #15). <b>Только чтение:</b> запросов, которые меняют данные в Dikidi,
/// в этом классе нет и быть не должно (ADR-0004).
/// <para>
/// В журнал сервера не пишем ни тела ответов, ни куки, ни пароль: в ответах журнала имена и телефоны пациентов.
/// </para>
/// </summary>
public sealed partial class DikidiWebClient(DikidiOptions options, TimeProvider clock) : IDikidiClient, IDisposable
{
    private const string SiteUrl = "https://dikidi.ru";
    private const string AuthUrl = "https://auth.dikidi.ru";
    private const string NetUrl = "https://dikidi.net";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    /// <summary>Куки входа, которые веб-кабинет переносит с dikidi.ru на dikidi.net.</summary>
    private static readonly string[] SessionCookies = ["token", "cookie_name", "uac", "cid", "session", "lang", "XSRF-TOKEN"];

    /// <summary>Признаки защиты от ботов: капчу не обходим, а останавливаемся с понятной ошибкой.</summary>
    private static readonly string[] BotWallMarkers = ["captcha", "cf-turnstile", "challenge-platform", "ddos-guard", "just a moment..."];

    /// <summary>Повторный вход не чаще этого: частые входы с одной учётки — верный путь к блокировке.</summary>
    private static readonly TimeSpan MinLoginInterval = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HttpClient? _http;
    private CookieContainer _cookies = new();
    private bool _loggedIn;
    private DateTimeOffset? _lastLoginAttempt;

    public async Task<DikidiJournal> GetJournalAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loggedIn)
                await LoginAsync(ct);

            var (lost, json) = await RequestJournalAsync(from, to, ct);
            if (lost)
            {
                // Сессия истекла — один повторный вход и один повтор запроса.
                _loggedIn = false;
                await LoginAsync(ct);
                (lost, json) = await RequestJournalAsync(from, to, ct);
                if (lost)
                    throw new DikidiException("Dikidi не пускает в журнал сразу после входа — у учётки нет прав на журнал или поменялся вход.");
            }

            return DikidiJournalParser.Parse(json);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Вход ────────────────────────────────────────────────────────────────

    private async Task LoginAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_lastLoginAttempt is DateTimeOffset last && now - last < MinLoginInterval)
            throw new DikidiException(
                $"Вход в Dikidi был {(int)(now - last).TotalMinutes} мин назад; следующий — не раньше чем через " +
                $"{(int)MinLoginInterval.TotalMinutes} мин после прошлого (защита учётки от блокировки).");
        _lastLoginAttempt = now;

        // Новый вход — с чистыми куками.
        _http?.Dispose();
        _cookies = new CookieContainer();
        _http = new HttpClient(new HttpClientHandler { CookieContainer = _cookies, UseCookies = true, AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(25),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9");

        var phone = new string(options.Phone.Where(char.IsAsciiDigit).ToArray());

        // Шаг 0: токен telegram_csrf со стартовой страницы.
        using var page = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{SiteUrl}/ru/"), "стартовая страница", ct);
        var html = await page.Content.ReadAsStringAsync(ct);
        CheckBotWall(page, html, "стартовая страница");
        var csrf = CsrfRegex().Match(html) is { Success: true } m1 ? m1.Groups[1].Value
            : CsrfFallbackRegex().Match(html) is { Success: true } m2 ? m2.Value
            : throw new DikidiException("На странице входа Dikidi не найден токен telegram_csrf — Dikidi поменял вход.");

        // Шаг 1: проверка номера. Ответ может дать второй токен csrf.
        var check = await PostFormAsync($"{AuthUrl}/ajax/check/auth/", SiteUrl,
            [new("telegram_csrf", csrf), new("number", phone)], "проверка номера", ct);
        var secondCsrf = TryJson(check) is JsonObject cj && cj["csrf"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : csrf;

        // Шаг 2: пароль.
        var auth = await PostFormAsync($"{AuthUrl}/ajax/user/auth/", SiteUrl,
            [new("telegram_csrf", csrf), new("number", phone), new("csrf", secondCsrf), new("password", options.Password), new("pdAgreement", "1")],
            "вход по паролю", ct);
        var authJson = TryJson(auth) as JsonObject;
        var ok = authJson?["callback"] is JsonArray callbacks
                 && callbacks.Any(x => x is JsonValue cv && cv.TryGetValue<string>(out var s) && s.Contains("sw.auth.complete"));
        if (!ok)
        {
            // Текст ошибки Dikidi («Неверный пароль») безопасен; остального ответа в журнал не пишем.
            var msg = authJson?["message"] is JsonValue mv && mv.TryGetValue<string>(out var t) ? $" Ответ: «{t}»." : "";
            throw new DikidiException("Dikidi не принял номер или пароль учётки адаптера." + msg);
        }

        // Шаг 3: перенос куки на dikidi.net и открытие журнала компании (инициализация сессии CRM).
        foreach (Cookie cookie in _cookies.GetAllCookies())
        {
            if (SessionCookies.Contains(cookie.Name))
                _cookies.Add(new Cookie(cookie.Name, cookie.Value, "/", ".dikidi.net"));
        }
        using var journalPage = await SendAsync(new HttpRequestMessage(HttpMethod.Get, JournalPageUrl), "открытие журнала", ct);
        CheckBotWall(journalPage, await journalPage.Content.ReadAsStringAsync(ct), "открытие журнала");

        _loggedIn = true;
    }

    // ── Журнал ──────────────────────────────────────────────────────────────

    private string JournalPageUrl => $"{NetUrl}/ru/owner/journal/?company={Uri.EscapeDataString(options.CompanyId)}";

    /// <summary>lost = true — сессия истекла (нужно войти заново).</summary>
    private async Task<(bool Lost, JsonNode? Json)> RequestJournalAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var url = $"{NetUrl}/ru/owner/ajax/journal/api/?company={Uri.EscapeDataString(options.CompanyId)}";
        var f = from.ToString("yyyy-MM-dd");
        var t = to.ToString("yyyy-MM-dd");
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(
            [
                new("request[get][]", "masters.info"),
                new("request[get][]", "masters.records"),
                new("request[options][masters][records][0][date_from]", f),
                new("request[options][masters][records][0][date_to]", t),
            ]),
        };
        AddAjaxHeaders(request, NetUrl, JournalPageUrl);
        var xsrf = _cookies.GetCookies(new Uri(NetUrl))["XSRF-TOKEN"];
        if (xsrf is not null)
            request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", xsrf.Value);

        using var response = await SendAsync(request, "журнал записей", ct, allowAuthErrors: true);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var body403 = await response.Content.ReadAsStringAsync(ct);
            CheckBotWall(response, body403, "журнал записей");
            return (true, null);
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            // На AJAX-запрос пришла HTML-страница: обычно это перенаправление на вход.
            CheckBotWall(response, body, "журнал записей");
            return (true, null);
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            throw new DikidiException("Журнал Dikidi вернул не JSON — формат изменился.");
        }

        // «Недостаточно прав» у Dikidi часто означает истёкшую сессию.
        if (json is JsonObject o && o["error"]?.ToString() == "1"
            && (o["message"]?.ToString() ?? "").Contains("Недостаточно прав", StringComparison.OrdinalIgnoreCase))
            return (true, null);

        return (false, json);
    }

    // ── Общее ───────────────────────────────────────────────────────────────

    private async Task<string> PostFormAsync(
        string url, string origin, KeyValuePair<string?, string?>[] form, string step, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        AddAjaxHeaders(request, origin, $"{origin}/ru/");
        using var response = await SendAsync(request, step, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        CheckBotWall(response, body, step);
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string step, CancellationToken ct, bool allowAuthErrors = false)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http!.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DikidiException($"Dikidi не ответил за 25 с ({step}).");
        }
        catch (HttpRequestException ex)
        {
            throw new DikidiException($"Нет связи с Dikidi ({step}): {ex.Message}");
        }

        var code = (int)response.StatusCode;
        if (allowAuthErrors && code is 401 or 403)
            return response;
        if (code is 404 or 405)
        {
            response.Dispose();
            throw new DikidiException($"Dikidi ответил {code} на шаге «{step}» — адрес изменился, адаптер надо обновить.");
        }
        if (code is 429)
        {
            response.Dispose();
            throw new DikidiException($"Dikidi просит реже обращаться (429, шаг «{step}»). Увеличьте Dikidi:IntervalMinutes.");
        }
        if (code >= 400)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            CheckBotWall(response, body, step);
            response.Dispose();
            throw new DikidiException($"Dikidi ответил ошибкой {code} на шаге «{step}».");
        }
        return response;
    }

    private static void AddAjaxHeaders(HttpRequestMessage request, string origin, string referer)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Headers.Referrer = new Uri(referer);
    }

    private static void CheckBotWall(HttpResponseMessage response, string body, string step)
    {
        var lower = body.Length > 20000 ? body[..20000].ToLowerInvariant() : body.ToLowerInvariant();
        var blocked = (int)response.StatusCode is 403 or 429 or 503 || lower.Contains("cloudflare");
        if (blocked && BotWallMarkers.Any(lower.Contains))
            throw new DikidiException(
                $"Dikidi показал защиту от ботов (капча) на шаге «{step}». Адаптер её не обходит: " +
                "войдите в Dikidi этой учёткой из браузера, подождите и проверьте позже.");
    }

    private static JsonNode? TryJson(string body)
    {
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex("""telegram_csrf["']?\s*[:=]\s*["']([a-f0-9]{32}:\d+\.\d+)["']""")]
    private static partial Regex CsrfRegex();

    [GeneratedRegex(@"[a-f0-9]{32}:\d{10}\.\d+")]
    private static partial Regex CsrfFallbackRegex();

    public void Dispose()
    {
        _http?.Dispose();
        _gate.Dispose();
    }
}
