namespace StaffMessenger.Services;

public static class ServerEndpoint
{
    public const string ApiBaseUrl = "https://droid-darkangel-staffmessenger-ced1.twc1.net";

    public static Uri ApiBaseUri { get; } = new(ApiBaseUrl);
}
