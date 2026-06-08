using StaffMessenger.Contracts.Attachments;

namespace StaffMessenger.Contracts.Bots;

[Flags]
public enum BotPermission
{
    None = 0,
    ReadMessages = 1,
    SendMessages = 2,
    UploadAttachments = 4,
    ManageWebhooks = 8
}

public sealed record CreateBotRequest(
    string Name,
    string Description,
    string? WebhookUrl,
    BotPermission Permissions);

public sealed record CreateBotResponse(
    Guid BotId,
    string Name,
    string ApiToken,
    string SigningSecret,
    DateTimeOffset TokenExpiresAt);

public sealed record BotInfoDto(
    Guid Id,
    string Name,
    string Description,
    string? WebhookUrl,
    BotPermission Permissions,
    DateTimeOffset CreatedAt);

public sealed record BotMessageRequest(
    Guid ConversationId,
    string Text,
    IReadOnlyList<Guid> AttachmentIds);

public sealed record BotConversationDto(
    Guid Id,
    string Title,
    string Kind,
    DateTimeOffset UpdatedAt);

public sealed record BotCommandRequest(
    Guid ConversationId,
    string Command,
    IReadOnlyDictionary<string, string> Arguments);

public sealed record BotCommandResponse(
    string Status,
    string Text,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record BotApiCapabilities(
    string ApiVersion,
    IReadOnlyList<string> Events,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<string> RequiredHeaders);

public sealed record BotWebhookEvent(
    string EventType,
    Guid ConversationId,
    Guid MessageId,
    string TextPreview,
    IReadOnlyList<AttachmentDto> Attachments,
    DateTimeOffset CreatedAt);
