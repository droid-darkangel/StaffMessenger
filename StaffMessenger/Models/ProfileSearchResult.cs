namespace StaffMessenger.Models;

public sealed record ProfileSearchResult(
    Guid Id,
    string DisplayName,
    string Username,
    string About,
    string Initials,
    string Accent);
