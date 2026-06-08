using Microsoft.AspNetCore.SignalR;
using StaffMessenger.Contracts.Realtime;
using StaffMessenger.Server.Security;

namespace StaffMessenger.Server.Realtime;

public sealed class MessageHub : Hub
{
    public Task JoinConversation(Guid conversationId)
        => Groups.AddToGroupAsync(Context.ConnectionId, conversationId.ToString("D"));
    

    public Task LeaveConversation(Guid conversationId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId.ToString("D"));

    public async Task Typing(Guid conversationId, bool isTyping)
    {
        var principal = Context.GetHttpContext()?.GetPrincipal();
        if (principal?.UserId is null)
            return;

        var payload = new TypingEvent(conversationId, principal.UserId.Value, isTyping, DateTimeOffset.UtcNow);
        await Clients.OthersInGroup(conversationId.ToString("D")).SendAsync("typing", payload);
    }
}
