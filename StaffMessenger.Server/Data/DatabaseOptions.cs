namespace StaffMessenger.Server.Data;

public sealed class DatabaseOptions
{
    public string ConnectionString { get; init; } =
        "Host=localhost;Port=5432;Database=staff_messenger;Username=postgres;Password=postgres";
}
