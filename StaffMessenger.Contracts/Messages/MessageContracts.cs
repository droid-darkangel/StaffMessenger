using StaffMessenger.Contracts.Attachments;
using StaffMessenger.Contracts.Crypto;

namespace StaffMessenger.Contracts.Messages;

public enum MessageDeliveryState
{
    Pending,
    Sent,
    Delivered,
    Read,
    Deleted
}

public sealed record SendMessageRequest(
    Guid ClientMessageId,
    string PlainPreview,
    CryptoEnvelope Body,
    IReadOnlyList<Guid> AttachmentIds,
    Guid? ReplyToMessageId);

public sealed record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid? SenderUserId,
    Guid? SenderBotId,
    string SenderDisplayName,
    string PlainPreview,
    CryptoEnvelope Body,
    IReadOnlyList<AttachmentDto> Attachments,
    DateTimeOffset SentAt,
    MessageDeliveryState State,
    Guid? ReplyToMessageId);

public enum DeleteMessageScope
{
    ForMe,
    ForEveryone
}

public sealed record DeleteMessageRequest(DeleteMessageScope Scope);
