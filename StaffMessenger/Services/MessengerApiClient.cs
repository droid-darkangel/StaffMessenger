using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using StaffMessenger.Contracts.App;
using StaffMessenger.Contracts.Attachments;
using StaffMessenger.Contracts.Auth;
using StaffMessenger.Contracts.Conversations;
using StaffMessenger.Contracts.Messages;
using StaffMessenger.Contracts.Realtime;

namespace StaffMessenger.Services;

public sealed class MessengerApiClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HttpClient _httpClient = new();
    private HubConnection? _hubConnection;

    public MessengerApiClient(Uri serverBaseUri)
        => _httpClient.BaseAddress = serverBaseUri;

    public event Func<MessageCreatedEvent, Task>? MessageCreated;

    public string? AccessToken { get; private set; }

    public async Task<StartAuthResponse> StartAuthAsync(
        StartAuthRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/start", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StartAuthResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Auth challenge response is empty.");
    }

    public async Task<AuthResponse> CompleteAuthAsync(
        CompleteAuthRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/complete", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Auth response is empty.");
        SetAccessToken(auth.AccessToken);
        return auth;
    }

    public async Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/register", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Auth response is empty.");
        SetAccessToken(auth.AccessToken);
        return auth;
    }

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/login", request, JsonOptions, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            throw new TwoFactorRequiredException();
        }

        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Auth response is empty.");
        SetAccessToken(auth.AccessToken);
        return auth;
    }

    public async Task<AuthResponse> RefreshSessionAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("/api/auth/session/refresh", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponse>(JsonOptions, cancellationToken)
                   ?? throw new InvalidOperationException("Auth response is empty.");
        SetAccessToken(auth.AccessToken);
        return auth;
    }

    public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<ConversationSummary>>("/api/conversations", JsonOptions, cancellationToken)
               ?? [];

    public async Task MarkConversationReadAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync($"/api/conversations/{conversationId:D}/read", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<UserProfileDto> GetProfileAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<UserProfileDto>("/api/auth/me", JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Profile response is empty.");

    public async Task<UserProfileDto> UpdateProfileAsync(
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/auth/me", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Profile response is empty.");
    }

    public async Task<UserProfileDto> UpdateSettingsAsync(
        UpdateUserSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PutAsJsonAsync("/api/auth/me/settings", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Profile response is empty.");
    }

    public async Task<UserProfileDto> LinkIdentityAsync(
        LinkIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/me/identities", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Profile response is empty.");
    }

    public async Task<UserProfileDto> UnlinkIdentityAsync(
        UnlinkIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/me/identities/unlink", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Profile response is empty.");
    }

    public async Task<YandexQrStartResponse> StartYandexIdentityLinkAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("/api/auth/me/identities/yandex/start", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<YandexQrStartResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Yandex QR response is empty.");
    }

    public async Task<UserProfileDto?> CompleteYandexIdentityLinkAsync(
        YandexQrCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/auth/me/identities/yandex/complete",
            request,
            JsonOptions,
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Profile response is empty.");
    }

    public async Task<StartIdentityVerificationResponse> StartIdentityVerificationAsync(
        StartIdentityVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/verification/start", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<StartIdentityVerificationResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Verification response is empty.");
    }

    public async Task<ConversationSummary> CreateDirectConversationAsync(
        string peerHandle,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            "/api/conversations/direct",
            new CreateDirectConversationRequest(peerHandle),
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ConversationSummary>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Conversation response is empty.");
    }

    public async Task<ConversationSummary> GetSavedConversationAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<ConversationSummary>(
               "/api/conversations/saved",
               JsonOptions,
               cancellationToken)
           ?? throw new InvalidOperationException("Saved conversation response is empty.");

    public async Task<ConversationSummary> GetAnnouncementConversationAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<ConversationSummary>(
               "/api/conversations/announcements",
               JsonOptions,
               cancellationToken)
           ?? throw new InvalidOperationException("Announcement conversation response is empty.");

    public async Task<IReadOnlyList<AuthSessionDto>> GetSessionsAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<AuthSessionDto>>(
               "/api/auth/me/sessions",
               JsonOptions,
               cancellationToken)
           ?? [];

    public async Task RevokeSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.DeleteAsync($"/api/auth/me/sessions/{sessionId:D}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task RevokeOtherSessionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("/api/auth/me/sessions/revoke-others", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<AppUpdateInfoDto> GetAppUpdateInfoAsync(CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<AppUpdateInfoDto>(
               "/api/app/info",
               JsonOptions,
               cancellationToken)
           ?? throw new InvalidOperationException("App info response is empty.");

    public async Task<TotpSetupResponse> SetupTotpAsync(CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync("/api/auth/me/2fa/setup", null, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TotpSetupResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("2FA setup response is empty.");
    }

    public async Task<UserProfileDto> EnableTotpAsync(
        EnableTotpRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/me/2fa/enable", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Profile response is empty.");
    }

    public async Task<UserProfileDto> DisableTotpAsync(
        DisableTotpRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync("/api/auth/me/2fa/disable", request, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<UserProfileDto>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Profile response is empty.");
    }

    public async Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var escaped = Uri.EscapeDataString(query);
        return await _httpClient.GetFromJsonAsync<IReadOnlyList<UserSearchResultDto>>(
                   $"/api/auth/users/search?q={escaped}",
                   JsonOptions,
                   cancellationToken)
               ?? [];
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
        => await _httpClient.GetFromJsonAsync<IReadOnlyList<MessageDto>>(
                   $"/api/conversations/{conversationId:D}/messages",
                   JsonOptions,
                   cancellationToken)
               ?? [];

    public async Task<MessageDto> SendMessageAsync(
        Guid conversationId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"/api/conversations/{conversationId:D}/messages",
            request,
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MessageDto>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Message response is empty.");
    }

    public async Task DeleteMessageAsync(
        Guid conversationId,
        Guid messageId,
        DeleteMessageScope scope,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/conversations/{conversationId:D}/messages/{messageId:D}")
        {
            Content = JsonContent.Create(new DeleteMessageRequest(scope), options: JsonOptions)
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteConversationAsync(
        Guid conversationId,
        DeleteConversationScope scope,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/conversations/{conversationId:D}")
        {
            Content = JsonContent.Create(new DeleteConversationRequest(scope), options: JsonOptions)
        };

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<AttachmentUploadResponse> UploadAttachmentAsync(
        Stream stream,
        string fileName,
        string contentType,
        AttachmentKind kind,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(content, "file", fileName);
        form.Add(new StringContent(kind.ToString()), "kind");

        var response = await _httpClient.PostAsync("/api/attachments", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AttachmentUploadResponse>(JsonOptions, cancellationToken)
               ?? throw new InvalidOperationException("Attachment response is empty.");
    }

    public async Task<byte[]> DownloadAttachmentAsync(string downloadUrl, CancellationToken cancellationToken = default)
        => await _httpClient.GetByteArrayAsync(downloadUrl, cancellationToken);

    public async Task ConnectRealtimeAsync(CancellationToken cancellationToken = default)
    {
        if (_hubConnection is not null)
            return;

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(_httpClient.BaseAddress!, "/hubs/messages"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<MessageCreatedEvent>("message.created", async payload =>
        {
            if (MessageCreated is not null)
                await MessageCreated(payload);
        });

        await _hubConnection.StartAsync(cancellationToken);
    }

    public async Task JoinConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (_hubConnection is null)
            await ConnectRealtimeAsync(cancellationToken);

        await _hubConnection!.InvokeAsync("JoinConversation", conversationId, cancellationToken);
    }

    public void SetAccessToken(string token)
    {
        AccessToken = token;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
            await _hubConnection.DisposeAsync();

        _httpClient.Dispose();
    }
}
