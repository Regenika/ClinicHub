using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClinicHub.Core.Dtos;

namespace ClinicHub.Admin.Services;

/// <summary>
/// HTTP-клиент API ClinicHub. Токены хранятся в Preferences.
/// Токен доступа живёт час; на ответ 401 клиент один раз продлевает вход refresh-токеном
/// (/api/auth/refresh) и повторяет запрос — иначе через час работы любое действие падало с 401.
/// </summary>
public class ApiClient
{
    private const string TokenKey = "clinichub_access_token";
    private const string RefreshKey = "clinichub_refresh_token";
    private const string BaseUrlKey = "clinichub_base_url";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string _token = "";

    public ApiClient(HttpClient http)
    {
        _http = http;
        _token = Preferences.Default.Get(TokenKey, "");
        BaseUrl = Preferences.Default.Get(BaseUrlKey, "http://localhost:5112");
    }

    public string BaseUrl { get; set; }

    public string? Token => string.IsNullOrEmpty(_token) ? null : _token;

    public bool IsAuthenticated => !string.IsNullOrEmpty(_token);

    public static void SaveBaseUrl(string url) => Preferences.Default.Set(BaseUrlKey, url);

    private HttpRequestMessage Build(string method, string path, object? body = null)
    {
        var msg = new HttpRequestMessage(new HttpMethod(method), BaseUrl.TrimEnd('/') + path);
        if (body != null)
            msg.Content = JsonContent.Create(body, options: JsonOptions);
        if (!string.IsNullOrEmpty(_token))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return msg;
    }

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Запрос собирается фабрикой: после продления входа его нужно собрать заново с новым токеном.</summary>
    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> build, bool canRefresh, CancellationToken ct = default)
    {
        var tokenUsed = _token;
        using (var msg = build())
        using (var resp = await _http.SendAsync(msg, ct))
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
                return string.IsNullOrWhiteSpace(text) ? default! : JsonSerializer.Deserialize<T>(text, JsonOptions)!;
            if (!(canRefresh && resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && await TryRefreshAsync(tokenUsed, ct)))
                throw new ApiException((int)resp.StatusCode, text);
        }

        using var retry = build();
        using var retryResp = await _http.SendAsync(retry, ct);
        var retryText = await retryResp.Content.ReadAsStringAsync(ct);
        if (!retryResp.IsSuccessStatusCode)
            throw new ApiException((int)retryResp.StatusCode, retryText);
        return string.IsNullOrWhiteSpace(retryText) ? default! : JsonSerializer.Deserialize<T>(retryText, JsonOptions)!;
    }

    /// <summary>Продлить вход refresh-токеном. Несколько одновременных 401 продлевают один раз.</summary>
    private async Task<bool> TryRefreshAsync(string expiredToken, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_token != expiredToken && !string.IsNullOrEmpty(_token))
                return true; // уже продлил другой запрос
            var refresh = Preferences.Default.Get(RefreshKey, "");
            if (string.IsNullOrEmpty(refresh))
                return false;
            using var msg = new HttpRequestMessage(HttpMethod.Post, BaseUrl.TrimEnd('/') + "/api/auth/refresh")
            {
                Content = JsonContent.Create(new RefreshRequest(refresh), options: JsonOptions),
            };
            using var resp = await _http.SendAsync(msg, ct);
            if (!resp.IsSuccessStatusCode)
                return false; // refresh тоже истёк — нужен новый вход
            var auth = await resp.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions, ct);
            if (auth is null || string.IsNullOrEmpty(auth.AccessToken))
                return false;
            await SetTokenAsync(auth);
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public Task<AuthResponse> LoginAsync(string login, string password, CancellationToken ct = default)
        => SendAsync<AuthResponse>(() => Build("POST", "/api/auth/login", new { login, password }), canRefresh: false, ct);

    public Task<T> GetAsync<T>(string path, CancellationToken ct = default)
        => SendAsync<T>(() => Build("GET", path), canRefresh: true, ct);

    public Task<T> PostAsync<T>(string path, object body, CancellationToken ct = default)
        => SendAsync<T>(() => Build("POST", path, body), canRefresh: true, ct);

    public Task<T> PutAsync<T>(string path, object body, CancellationToken ct = default)
        => SendAsync<T>(() => Build("PUT", path, body), canRefresh: true, ct);

    public Task<T> DeleteAsync<T>(string path, CancellationToken ct = default)
        => SendAsync<T>(() => Build("DELETE", path), canRefresh: true, ct);

    public Task SetTokenAsync(AuthResponse auth)
    {
        _token = auth.AccessToken;
        Preferences.Default.Set(TokenKey, auth.AccessToken);
        Preferences.Default.Set(RefreshKey, auth.RefreshToken);
        return Task.CompletedTask;
    }

    public void ClearToken()
    {
        _token = "";
        Preferences.Default.Remove(TokenKey);
        Preferences.Default.Remove(RefreshKey);
    }
}

public class ApiException : Exception
{
    public int StatusCode { get; }
    public ApiException(int statusCode, string body)
        : base($"Сервер вернул {statusCode}: {body}")
    {
        StatusCode = statusCode;
    }
}