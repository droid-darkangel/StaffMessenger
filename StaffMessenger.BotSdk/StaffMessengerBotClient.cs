using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using StaffMessenger.Contracts.Bots;
using StaffMessenger.Contracts.Messages;
using StaffMessenger.Contracts.Realtime;

namespace StaffMessenger.BotSdk;

public sealed class StaffMessengerBotClient : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _apiToken;
    private HubConnection? _hubConnection;

    public StaffMessengerBotClient(Uri serverBaseUri, string apiToken, HttpMessageHandler? handler = null)
    {
        _apiToken = apiToken;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = serverBaseUri;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
    }

    public event Func<MessageCreatedEvent, Task>? MessageCreated;

    public async Task<BotApiCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<BotApiCapabilities>("/api/bots/capabilities", cancellationToken)
               ?? throw new InvalidOperationException("Server returned empty bot capabilities.");
    }

    public async Task<IReadOnlyList<BotConversationDto>> GetConversationsAsync(CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<IReadOnlyList<BotConversationDto>>("/api/bots/conversations", cancellationToken)
               ?? [];
    }

    public async Task JoinApiConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsync($"/api/bots/conversations/{conversationId:D}/join", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
        Guid conversationId,
        int limit = 80,
        CancellationToken cancellationToken = default)
    {
        return await _httpClient.GetFromJsonAsync<IReadOnlyList<MessageDto>>(
                   $"/api/bots/conversations/{conversationId:D}/messages?limit={limit}",
                   cancellationToken)
               ?? [];
    }

    public async Task<MessageDto> SendTextAsync(
        Guid conversationId,
        string text,
        IReadOnlyList<Guid>? attachmentIds = null,
        CancellationToken cancellationToken = default)
    {
        var request = new BotMessageRequest(conversationId, text, attachmentIds ?? []);
        var response = await _httpClient.PostAsJsonAsync("/api/bots/messages", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MessageDto>(cancellationToken: cancellationToken)
               ?? throw new InvalidOperationException("Server returned an empty message.");
    }

    public async Task ConnectRealtimeAsync(CancellationToken cancellationToken = default)
    {
        if (_hubConnection is not null)
        {
            return;
        }

        var hubUri = new Uri(_httpClient.BaseAddress!, "/hubs/messages");
        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUri, options => options.AccessTokenProvider = () => Task.FromResult<string?>(_apiToken))
            .WithAutomaticReconnect()
            .Build();

        _hubConnection.On<MessageCreatedEvent>("message.created", async payload =>
        {
            if (MessageCreated is not null)
            {
                await MessageCreated(payload);
            }
        });

        await _hubConnection.StartAsync(cancellationToken);
    }

    public async Task JoinConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        if (_hubConnection is null)
        {
            await ConnectRealtimeAsync(cancellationToken);
        }

        await _hubConnection!.InvokeAsync("JoinConversation", conversationId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
        }

        _httpClient.Dispose();
    }
}
