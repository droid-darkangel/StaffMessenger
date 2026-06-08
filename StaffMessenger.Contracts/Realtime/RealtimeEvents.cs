using StaffMessenger.Contracts.Messages;

namespace StaffMessenger.Contracts.Realtime;

public sealed record MessageCreatedEvent(MessageDto Message);

public sealed record TypingEvent(
    Guid ConversationId,
    Guid UserId,
    bool IsTyping,
    DateTimeOffset At);

public sealed record PresenceEvent(
    Guid UserId,
    string Status,
    DateTimeOffset At);
