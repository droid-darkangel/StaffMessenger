using System.Text.Json;

namespace StaffMessenger.Services;

public sealed record LocalSession(string AccessToken);

public static class LocalSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SessionDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StaffMessenger");

    private static string SessionPath => Path.Combine(SessionDirectory, "session.json");

    public static LocalSession? Load()
    {
        if (!File.Exists(SessionPath))
            return null;

        try
        {
            var json = File.ReadAllText(SessionPath);
            var session = JsonSerializer.Deserialize<LocalSession>(json, JsonOptions);
            if (session is null || string.IsNullOrWhiteSpace(session.AccessToken))
            {
                Clear();
                return null;
            }

            return session;
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            Clear();
            return null;
        }
    }

    public static void Save(LocalSession session)
    {
        Directory.CreateDirectory(SessionDirectory);
        File.WriteAllText(SessionPath, JsonSerializer.Serialize(session, JsonOptions));
    }

    public static void Clear()
    {
        if (File.Exists(SessionPath))
            File.Delete(SessionPath);
    }
}
