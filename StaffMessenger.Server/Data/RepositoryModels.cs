using StaffMessenger.Contracts.Bots;

namespace StaffMessenger.Server.Data;

public sealed record UserRecord(
    Guid Id,
    string Handle,
    string DisplayName,
    string PasswordHash);

public sealed record AuthIdentityRecord(
    Guid UserId,
    string Handle,
    string DisplayName,
    string? PasswordHash,
    bool TwoFactorEnabled,
    string? TotpSecret);

public sealed record PrincipalRecord(
    Guid? SessionId,
    Guid? UserId,
    Guid? BotId,
    string DisplayName,
    string Handle,
    bool IsBot,
    DateTimeOffset ExpiresAt);

public sealed record YandexDeviceChallengeRecord(
    Guid Id,
    Guid? UserId,
    string DeviceCode,
    string UserCode,
    string VerificationUrl,
    DateTimeOffset ExpiresAt);

public sealed record StoredAttachment(
    Guid Id,
    Guid OwnerUserId,
    string StoragePath,
    string FileName,
    string ContentType,
    long SizeBytes);

public sealed record CreatedBotRecord(
    Guid BotId,
    string Name,
    string Token,
    string SigningSecret,
    DateTimeOffset ExpiresAt,
    BotPermission Permissions);
