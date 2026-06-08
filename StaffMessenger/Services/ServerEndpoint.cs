namespace StaffMessenger.Services;

public static class ServerEndpoint
{
    public const string ApiBaseUrl = "http://5.42.114.194:8000";

    public static Uri ApiBaseUri { get; } = new(ApiBaseUrl);
}
