namespace StaffMessenger.Contracts.App;

public sealed record AppUpdateInfoDto(
    string ProductName,
    string CurrentVersion,
    string LatestVersion,
    bool UpdateAvailable,
    string ReleaseNotes,
    string? DownloadUrl);
