namespace StaffMessenger.Server.Security;

public static class HttpContextPrincipalExtensions
{
    private const string PrincipalKey = "StaffMessenger.Principal";

    public static void SetPrincipal(this HttpContext context, RequestPrincipal principal)
        => context.Items[PrincipalKey] = principal;

    public static RequestPrincipal? GetPrincipal(this HttpContext context)
        => context.Items.TryGetValue(PrincipalKey, out var value) ? value as RequestPrincipal : null;
}
