using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using StaffMessenger.Contracts.Auth;

namespace StaffMessenger.Server.Security;

public sealed class YandexOAuthService
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient = new();

    public YandexOAuthService(IConfiguration configuration)
        => _configuration = configuration;

    public async Task<string> ResolveIdentifierAsync(
        string fallbackIdentifier,
        string code,
        CancellationToken cancellationToken = default)
    {
        var clientId = _configuration["Authentication:Yandex:ClientId"];
        var clientSecret = _configuration["Authentication:Yandex:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret)
            || string.IsNullOrWhiteSpace(code)
            || code.StartsWith("oauth-dev", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Yandex OAuth is not configured.");
        }

        using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        });

        using var tokenResponse = await _httpClient.PostAsync("https://oauth.yandex.ru/token", tokenContent, cancellationToken);
        tokenResponse.EnsureSuccessStatusCode();
        var token = await tokenResponse.Content.ReadFromJsonAsync<YandexTokenResponse>(cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Yandex OAuth token response is empty.");

        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, "https://login.yandex.ru/info?format=json");
        infoRequest.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token.AccessToken);
        using var infoResponse = await _httpClient.SendAsync(infoRequest, cancellationToken);
        infoResponse.EnsureSuccessStatusCode();
        var info = await infoResponse.Content.ReadFromJsonAsync<YandexUserInfo>(cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException("Yandex profile response is empty.");

        return !string.IsNullOrWhiteSpace(info.DefaultEmail)
            ? info.DefaultEmail
            : !string.IsNullOrWhiteSpace(info.Login)
                ? info.Login
                : info.Id ?? fallbackIdentifier;
    }

    public async Task<YandexDeviceCodeResponse> StartDeviceCodeAsync(
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        var clientId = _configuration["Authentication:Yandex:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) || clientId.StartsWith("<", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Yandex OAuth client id is not configured.");
        }

        var scope = _configuration["Authentication:Yandex:Scope"] ?? "login:info login:email";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["device_id"] = deviceId,
            ["device_name"] = deviceName,
            ["scope"] = scope
        });

        using var response = await _httpClient.PostAsync("https://oauth.yandex.ru/device/code", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<YandexDeviceCodePayload>(cancellationToken: cancellationToken)
                      ?? throw new InvalidOperationException("Yandex device code response is empty.");

        return new YandexDeviceCodeResponse(
            payload.DeviceCode,
            payload.UserCode,
            payload.VerificationUrl,
            DateTimeOffset.UtcNow.AddSeconds(payload.ExpiresIn),
            payload.Interval);
    }

    public async Task<string> ResolveIdentifierFromDeviceCodeAsync(
        string deviceCode,
        CancellationToken cancellationToken = default)
    {
        var clientId = _configuration["Authentication:Yandex:ClientId"];
        var clientSecret = _configuration["Authentication:Yandex:ClientSecret"];
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Yandex OAuth is not configured.");
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "device_code",
            ["code"] = deviceCode,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret
        });

        using var response = await _httpClient.PostAsync("https://oauth.yandex.ru/token", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadFromJsonAsync<YandexOAuthError>(cancellationToken: cancellationToken);
            if (string.Equals(error?.Error, "authorization_pending", StringComparison.OrdinalIgnoreCase))
            {
                throw new YandexAuthorizationPendingException();
            }

            throw new HttpRequestException($"Yandex OAuth token request failed: {error?.Error ?? response.StatusCode.ToString()}");
        }

        var token = await response.Content.ReadFromJsonAsync<YandexTokenResponse>(cancellationToken: cancellationToken)
                    ?? throw new InvalidOperationException("Yandex OAuth token response is empty.");

        return await ResolveIdentifierFromAccessTokenAsync(token.AccessToken, cancellationToken);
    }

    private async Task<string> ResolveIdentifierFromAccessTokenAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, "https://login.yandex.ru/info?format=json");
        infoRequest.Headers.Authorization = new AuthenticationHeaderValue("OAuth", accessToken);
        using var infoResponse = await _httpClient.SendAsync(infoRequest, cancellationToken);
        infoResponse.EnsureSuccessStatusCode();
        var info = await infoResponse.Content.ReadFromJsonAsync<YandexUserInfo>(cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException("Yandex profile response is empty.");

        return !string.IsNullOrWhiteSpace(info.DefaultEmail)
            ? info.DefaultEmail
            : !string.IsNullOrWhiteSpace(info.Login)
                ? info.Login
                : info.Id ?? throw new InvalidOperationException("Yandex profile has no stable identifier.");
    }

    private sealed record YandexTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken);

    private sealed record YandexDeviceCodePayload(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_url")] string VerificationUrl,
        [property: JsonPropertyName("interval")] int Interval,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private sealed record YandexOAuthError(
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);

    private sealed record YandexUserInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("default_email")] string? DefaultEmail);
}

public sealed record YandexDeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUrl,
    DateTimeOffset ExpiresAt,
    int IntervalSeconds);

public sealed class YandexAuthorizationPendingException : Exception
{
    public YandexAuthorizationPendingException()
        : base("Yandex authorization is still pending.")
    {
    }
}
