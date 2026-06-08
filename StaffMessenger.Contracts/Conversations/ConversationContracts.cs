namespace StaffMessenger.Contracts.Conversations;

public enum ConversationKind
{
    Direct,
    Group,
    Bot,
    Saved,
    Announcement
}

public sealed record MemberSummary(
    Guid UserId,
    string Handle,
    string DisplayName,
    string? AvatarUrl,
    bool IsOnline,
    DateTimeOffset? LastSeenAt);

public sealed record ConversationSummary(
    Guid Id,
    ConversationKind Kind,
    string Title,
    string? AvatarUrl,
    string LastPreview,
    int UnreadCount,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<MemberSummary> Members);

public sealed record CreateDirectConversationRequest(string PeerHandle);

public sealed record CreateGroupConversationRequest(
    string Title,
    IReadOnlyList<string> MemberHandles);

public sealed record BroadcastAnnouncementRequest(string Text);

public enum DeleteConversationScope
{
    ForMe,
    ForEveryone
}

public sealed record DeleteConversationRequest(DeleteConversationScope Scope);
