namespace StaffMessenger.Services;

public static class ServerEndpoint
{
    public const string ApiBaseUrl = "http://72.56.235.188:5072";

    public static Uri ApiBaseUri { get; } = new(ApiBaseUrl);
}
