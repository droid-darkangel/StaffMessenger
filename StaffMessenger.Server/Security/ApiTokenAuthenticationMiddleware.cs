using StaffMessenger.Crypto.Identity;
using StaffMessenger.Server.Data;

namespace StaffMessenger.Server.Security;

public sealed class ApiTokenAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    public ApiTokenAuthenticationMiddleware(RequestDelegate next)
        => _next = next;

    public async Task InvokeAsync(HttpContext context, MessengerRepository repository)
    {
        var token = ExtractToken(context);
        if (!string.IsNullOrWhiteSpace(token))
        {
            var hash = PasswordHasher.HashOpaqueToken(token);
            var principal = await repository.ResolvePrincipalAsync(hash, context.RequestAborted);
            if (principal is not null)
            {
                context.SetPrincipal(new RequestPrincipal(
                    principal.SessionId,
                    principal.UserId,
                    principal.BotId,
                    principal.DisplayName,
                    principal.Handle,
                    principal.IsBot,
                    principal.ExpiresAt));

                if (principal.SessionId is not null)
                {
                    await repository.TouchSessionAsync(principal.SessionId.Value, context.RequestAborted);
                }
            }
        }

        await _next(context);
    }

    private static string? ExtractToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization["Bearer ".Length..].Trim();

        if (context.Request.Path.StartsWithSegments("/hubs/messages")
            && context.Request.Query.TryGetValue("access_token", out var token))
            return token.ToString();

        return null;
    }
}
