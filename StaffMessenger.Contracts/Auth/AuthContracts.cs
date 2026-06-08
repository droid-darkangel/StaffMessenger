using StaffMessenger.Contracts.Crypto;

namespace StaffMessenger.Contracts.Auth;

public enum AuthProvider
{
    YandexId,
    Phone,
    Email
}

public enum AuthIdentityStatus
{
    Pending,
    Verified
}

public sealed record RegisterRequest(
    string DisplayName,
    string Handle,
    AuthProvider Provider,
    string Identifier,
    string Password,
    PublicDeviceKey? DeviceKey);

public sealed record LoginRequest(
    AuthProvider Provider,
    string Identifier,
    string Password,
    string? TotpCode,
    PublicDeviceKey? DeviceKey);

public sealed record TwoFactorRequiredResponse(
    bool RequiresTwoFactor,
    string Message);

public sealed record AuthResponse(
    Guid UserId,
    string Handle,
    string DisplayName,
    string AccessToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<PublicDeviceKey> DeviceKeys);

public sealed record StartAuthRequest(
    AuthProvider Provider,
    string Identifier,
    string? RedirectUri);

public sealed record StartAuthResponse(
    Guid ChallengeId,
    AuthProvider Provider,
    string Status,
    string? AuthorizationUrl,
    string? DevelopmentCode);

public sealed record CompleteAuthRequest(
    Guid ChallengeId,
    AuthProvider Provider,
    string Identifier,
    string Code,
    string? TotpCode,
    string? DisplayName,
    PublicDeviceKey? DeviceKey);

public sealed record YandexQrStartResponse(
    Guid ChallengeId,
    string UserCode,
    string VerificationUrl,
    string QrPayload,
    DateTimeOffset ExpiresAt,
    int PollIntervalSeconds);

public sealed record YandexQrCompleteRequest(
    Guid ChallengeId,
    string? TotpCode,
    PublicDeviceKey? DeviceKey);

public sealed record LinkIdentityRequest(
    AuthProvider Provider,
    string Identifier,
    string? Password,
    string? VerificationCode);

public sealed record UnlinkIdentityRequest(
    AuthProvider Provider,
    string Identifier);

public sealed record StartIdentityVerificationRequest(
    AuthProvider Provider,
    string Identifier);

public sealed record StartIdentityVerificationResponse(
    AuthProvider Provider,
    string Identifier,
    string Status,
    DateTimeOffset ExpiresAt,
    string? DevelopmentCode);

public sealed record AuthIdentityDto(
    AuthProvider Provider,
    string Identifier,
    AuthIdentityStatus Status,
    DateTimeOffset LinkedAt);

public sealed record AuthSessionDto(
    Guid Id,
    string DeviceName,
    string UserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

public sealed record UserPrivacySettingsDto(
    bool AllowProfileSearch,
    bool AllowPhoneDiscovery,
    bool ShowBirthday,
    bool ShowLastSeen,
    bool SendReadReceipts,
    bool EncryptMediaMetadata);

public sealed record UserNotificationSettingsDto(
    bool ShowMessageNotifications,
    bool PlayIncomingSound,
    bool ShowTextPreview);

public sealed record UpdateUserSettingsRequest(
    UserPrivacySettingsDto Privacy,
    UserNotificationSettingsDto Notifications);

public sealed record TotpSetupResponse(
    string Secret,
    string OtpAuthUri,
    string Phone);

public sealed record EnableTotpRequest(
    string Secret,
    string Phone,
    string Code);

public sealed record DisableTotpRequest(string Code);

public sealed record UserProfileDto(
    Guid Id,
    string Handle,
    string DisplayName,
    string? AvatarUrl,
    string? About,
    DateOnly? BirthDate,
    string? Phone,
    string Status,
    bool TwoFactorEnabled,
    IReadOnlyList<AuthIdentityDto> Identities,
    UserPrivacySettingsDto Privacy,
    UserNotificationSettingsDto Notifications,
    IReadOnlyList<PublicDeviceKey> DeviceKeys);

public sealed record UpdateProfileRequest(
    string DisplayName,
    string Handle,
    string? AvatarUrl,
    string? About,
    DateOnly? BirthDate,
    string? Phone);

public sealed record UserSearchResultDto(
    Guid Id,
    string Handle,
    string DisplayName,
    string? AvatarUrl,
    string? About,
    bool IsOnline);
