namespace StaffMessenger.Server.Security;

public sealed record RequestPrincipal(
    Guid? SessionId,
    Guid? UserId,
    Guid? BotId,
    string DisplayName,
    string Handle,
    bool IsBot,
    DateTimeOffset ExpiresAt)
{
    public bool IsUser => UserId.HasValue && !IsBot;
}
