using System.Text.Json;

namespace StaffMessenger.Services;

public sealed record ClientSettings(
    double ChatFontSize,
    string ChatThemeName,
    string BubbleDensity,
    bool SendByEnter)
{
    public static ClientSettings Default { get; } = new(
        14,
        "Светлая",
        "Комфортная",
        true);
}

public static class ClientSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StaffMessenger");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "client-settings.json");

    public static ClientSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return ClientSettings.Default;

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<ClientSettings>(json, JsonOptions) ?? ClientSettings.Default;
        }
        catch (IOException)
        {
            return ClientSettings.Default;
        }
        catch (JsonException)
        {
            return ClientSettings.Default;
        }
    }

    public static void Save(ClientSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
