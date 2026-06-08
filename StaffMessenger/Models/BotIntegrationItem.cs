namespace StaffMessenger.Models;

public sealed record BotIntegrationItem(
    string Name,
    string Description,
    string Status,
    string Accent);
