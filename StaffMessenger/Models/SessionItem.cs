using StaffMessenger.Contracts.Auth;

namespace StaffMessenger.Models;

public sealed record SessionItem(
    Guid Id,
    string DeviceName,
    string UserAgent,
    string CreatedText,
    string LastSeenText,
    string ExpiresText,
    bool IsCurrent)
{
    public string CurrentBadge => IsCurrent ? "это устройство" : "";

    public static SessionItem FromDto(AuthSessionDto dto)
    {
        return new SessionItem(
            dto.Id,
            dto.DeviceName,
            dto.UserAgent,
            dto.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy, HH:mm"),
            dto.LastSeenAt.ToLocalTime().ToString("dd.MM.yyyy, HH:mm"),
            dto.ExpiresAt.ToLocalTime().ToString("dd.MM.yyyy, HH:mm"),
            dto.IsCurrent);
    }
}
