using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using StaffMessenger.Contracts.Auth;
using StaffMessenger.Crypto.Identity;
using StaffMessenger.Server.Data;
using StaffMessenger.Server.Security;
using StaffMessenger.Server.Services;

namespace StaffMessenger.Server.Endpoints;

public static class AuthEndpoints
{
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static readonly Regex E164PhoneRegex = new(@"^\+[1-9]\d{7,14}$", RegexOptions.Compiled);
    private static readonly Regex HandleRegex = new(@"^[a-zA-Z0-9_]{3,32}$", RegexOptions.Compiled);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/start", ([FromBody] StartAuthRequest request, IConfiguration configuration) =>
        {
            var identifier = NormalizeIdentifier(request.Provider, request.Identifier);
            if (!IsIdentifierValid(request.Provider, identifier))
                return Results.BadRequest(new { error = "Identifier is invalid for selected provider." });

            var challengeId = Guid.NewGuid();
            var authorizationUrl = request.Provider == AuthProvider.YandexId
                ? BuildYandexAuthorizationUrl(configuration, challengeId, request.RedirectUri)
                : null;

            return Results.Ok(new StartAuthResponse(
                challengeId,
                request.Provider,
                request.Provider switch
                {
                    AuthProvider.YandexId => "Open YandexID OAuth URL. Password is not required.",
                    AuthProvider.Phone => "Phone identity can be linked after password confirmation.",
                    AuthProvider.Email => "Email identity can be linked after password confirmation.",
                    _ => "Challenge started."
                },
                authorizationUrl,
                null));
        });

        group.MapPost("/complete", async (
            HttpContext context,
            [FromBody] CompleteAuthRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] TokenService tokenService,
            [FromServices] TotpService totpService,
            [FromServices] YandexOAuthService yandexOAuth,
            CancellationToken cancellationToken) =>
        {
            if (request.Provider != AuthProvider.YandexId)
                return Results.BadRequest(new { error = "Use /api/auth/login for phone and email password login." });

            string identifier;
            try
            {
                identifier = NormalizeIdentifier(
                    request.Provider,
                    await yandexOAuth.ResolveIdentifierAsync(request.Identifier, request.Code, cancellationToken));
            }
            catch (HttpRequestException)
            {
                return Results.Unauthorized();
            }
            catch (InvalidOperationException)
            {
                return Results.Unauthorized();
            }

            if (!IsIdentifierValid(request.Provider, identifier))
                return Results.BadRequest(new { error = "Identifier is invalid for selected provider." });

            var identity = await repository.FindIdentityAsync(AuthProvider.YandexId, identifier, cancellationToken);
            if (identity is null && EmailRegex.IsMatch(identifier))
            {
                var emailIdentity = await repository.FindIdentityByVerifiedEmailAsync(identifier, cancellationToken);
                if (emailIdentity is not null)
                {
                    await repository.LinkIdentityAsync(emailIdentity.UserId, AuthProvider.YandexId, identifier, null, cancellationToken);
                    identity = emailIdentity;
                }
            }

            if (identity is null)
            {
                var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                    ? BuildDisplayName(request.Provider, identifier)
                    : request.DisplayName.Trim();
                var handle = BuildInitialHandle(identifier);
                var user = await repository.CreateUserAsync(
                    new RegisterRequest(displayName, handle, AuthProvider.YandexId, identifier, $"yandex-{Guid.NewGuid():N}", request.DeviceKey),
                    $"external:{AuthProvider.YandexId}",
                    cancellationToken);
                identity = new AuthIdentityRecord(user.Id, user.Handle, user.DisplayName, null, false, null);
            }

            if (!ValidateTotpIfNeeded(identity, request.TotpCode, totpService))
                return Results.Unauthorized();

            if (request.DeviceKey is not null)
                await repository.UpsertDeviceKeyAsync(identity.UserId, request.DeviceKey, cancellationToken);

            return Results.Ok(await CreateAuthResponseAsync(
                identity.UserId,
                repository,
                tokenService,
                context,
                request.DeviceKey,
                cancellationToken));
        });

        group.MapPost("/yandex/device/start", async (
            [FromServices] MessengerRepository repository,
            [FromServices] YandexOAuthService yandexOAuth,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var challenge = await yandexOAuth.StartDeviceCodeAsync(
                    $"staffmessenger-{Guid.NewGuid():N}",
                    "NewSunshine StaffMessenger",
                    cancellationToken);
                var challengeId = await repository.CreateYandexDeviceChallengeAsync(
                    null,
                    challenge.DeviceCode,
                    challenge.UserCode,
                    challenge.VerificationUrl,
                    challenge.ExpiresAt,
                    cancellationToken);

                return Results.Ok(new YandexQrStartResponse(
                    challengeId,
                    challenge.UserCode,
                    challenge.VerificationUrl,
                    challenge.VerificationUrl,
                    challenge.ExpiresAt,
                    challenge.IntervalSeconds));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (HttpRequestException)
            {
                return Results.Problem("Yandex OAuth is unavailable.", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapPost("/yandex/device/complete", async (
            HttpContext context,
            [FromBody] YandexQrCompleteRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] TokenService tokenService,
            [FromServices] TotpService totpService,
            [FromServices] YandexOAuthService yandexOAuth,
            CancellationToken cancellationToken) =>
        {
            var challenge = await repository.GetYandexDeviceChallengeAsync(request.ChallengeId, null, cancellationToken);
            if (challenge is null)
                return Results.NotFound(new { error = "Yandex QR challenge was not found or expired." });

            string identifier;
            try
            {
                identifier = NormalizeIdentifier(
                    AuthProvider.YandexId,
                    await yandexOAuth.ResolveIdentifierFromDeviceCodeAsync(challenge.DeviceCode, cancellationToken));
            }
            catch (YandexAuthorizationPendingException)
            {
                return Results.Accepted(value: new { pending = true, message = "Yandex authorization is pending." });
            }
            catch (HttpRequestException)
            {
                return Results.Unauthorized();
            }
            catch (InvalidOperationException)
            {
                return Results.Unauthorized();
            }

            var identity = await ResolveOrCreateYandexIdentityAsync(
                identifier,
                null,
                request.DeviceKey,
                repository,
                cancellationToken);
            if (!ValidateTotpIfNeeded(identity, request.TotpCode, totpService))
            {
                if (identity.TwoFactorEnabled && string.IsNullOrWhiteSpace(request.TotpCode))
                    return Results.Accepted(value: new TwoFactorRequiredResponse(
                        true,
                        "Yandex accepted. One-time 2FA PIN is required."));

                return Results.Unauthorized();
            }

            if (request.DeviceKey is not null)
                await repository.UpsertDeviceKeyAsync(identity.UserId, request.DeviceKey, cancellationToken);

            await repository.CompleteYandexDeviceChallengeAsync(request.ChallengeId, cancellationToken);
            return Results.Ok(await CreateAuthResponseAsync(
                identity.UserId,
                repository,
                tokenService,
                context,
                request.DeviceKey,
                cancellationToken));
        });

        group.MapPost("/register", async (
            HttpContext context,
            [FromBody] RegisterRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] PasswordHasher passwordHasher,
            [FromServices] TokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var identifier = NormalizeIdentifier(request.Provider, request.Identifier);
            if (!IsPasswordProvider(request.Provider)
                || !IsIdentifierValid(request.Provider, identifier)
                || string.IsNullOrWhiteSpace(request.DisplayName)
                || !HandleRegex.IsMatch(request.Handle.Trim())
                || string.IsNullOrWhiteSpace(request.Password))
                return Results.BadRequest(new { error = "Display name, username, password and valid phone/email are required." });

            try
            {
                var passwordHash = passwordHasher.HashPassword(request.Password);
                var user = await repository.CreateUserAsync(
                    request with { Identifier = identifier, Handle = request.Handle.Trim().TrimStart('@') },
                    passwordHash,
                    cancellationToken);
                return Results.Ok(await CreateAuthResponseAsync(
                    user.Id,
                    repository,
                    tokenService,
                    context,
                    request.DeviceKey,
                    cancellationToken));
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "Username or identity is already linked." });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        group.MapPost("/login", async (
            HttpContext context,
            [FromBody] LoginRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] PasswordHasher passwordHasher,
            [FromServices] TokenService tokenService,
            [FromServices] TotpService totpService,
            CancellationToken cancellationToken) =>
        {
            if (!IsPasswordProvider(request.Provider))
                return Results.BadRequest(new { error = "YandexID login does not use password." });

            var identifier = NormalizeIdentifier(request.Provider, request.Identifier);
            if (!IsIdentifierValid(request.Provider, identifier))
                return Results.BadRequest(new { error = "Identifier is invalid for selected provider." });

            var identity = await repository.FindIdentityAsync(request.Provider, identifier, cancellationToken);
            if (identity is null
                || string.IsNullOrWhiteSpace(identity.PasswordHash)
                || !passwordHasher.VerifyPassword(request.Password, identity.PasswordHash))
                return Results.Unauthorized();

            if (identity.TwoFactorEnabled && string.IsNullOrWhiteSpace(request.TotpCode))
                return Results.Accepted(value: new TwoFactorRequiredResponse(
                    true,
                    "Password accepted. One-time 2FA PIN is required."));

            if (!ValidateTotpIfNeeded(identity, request.TotpCode, totpService))
                return Results.Unauthorized();

            if (request.DeviceKey is not null)
                await repository.UpsertDeviceKeyAsync(identity.UserId, request.DeviceKey, cancellationToken);

            return Results.Ok(await CreateAuthResponseAsync(
                identity.UserId,
                repository,
                tokenService,
                context,
                request.DeviceKey,
                cancellationToken));
        });

        group.MapPost("/session/refresh", async (
            HttpContext context,
            [FromServices] MessengerRepository repository,
            [FromServices] TokenService tokenService,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var oldToken = ExtractBearerToken(context);
            if (!string.IsNullOrWhiteSpace(oldToken))
                await repository.RevokeSessionAsync(PasswordHasher.HashOpaqueToken(oldToken), cancellationToken);

            return Results.Ok(await CreateAuthResponseAsync(
                principal.UserId.Value,
                repository,
                tokenService,
                context,
                null,
                cancellationToken));
        });

        group.MapGet("/me", async (HttpContext context, [FromServices] MessengerRepository repository, CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var profile = await repository.GetProfileAsync(principal.UserId.Value, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapGet("/me/sessions", async (
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var sessions = await repository.GetSessionsAsync(
                principal.UserId.Value,
                principal.SessionId,
                cancellationToken);
            return Results.Ok(sessions);
        });

        group.MapDelete("/me/sessions/{sessionId:guid}", async (
            [FromRoute] Guid sessionId,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            await repository.RevokeSessionByIdAsync(principal.UserId.Value, sessionId, cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/me/sessions/revoke-others", async (
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null || principal.SessionId is null)
                return Results.Unauthorized();

            await repository.RevokeOtherSessionsAsync(
                principal.UserId.Value,
                principal.SessionId.Value,
                cancellationToken);
            return Results.NoContent();
        });

        group.MapPut("/me", async (
            HttpContext context,
            [FromBody] UpdateProfileRequest request,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            try
            {
                var profile = await repository.UpdateProfileAsync(principal.UserId.Value, request, cancellationToken);
                return profile is null ? Results.NotFound() : Results.Ok(profile);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return Results.Conflict(new { error = "Username is already taken." });
            }
        });

        group.MapPut("/me/settings", async (
            HttpContext context,
            [FromBody] UpdateUserSettingsRequest request,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var profile = await repository.UpdateSettingsAsync(principal.UserId.Value, request, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapPost("/me/identities", async (
            HttpContext context,
            [FromBody] LinkIdentityRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] PasswordHasher passwordHasher,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var identifier = NormalizeIdentifier(request.Provider, request.Identifier);
            if (!IsIdentifierValid(request.Provider, identifier))
                return Results.BadRequest(new { error = "Identifier is invalid for selected provider." });

            var passwordHash = IsPasswordProvider(request.Provider)
                ? string.IsNullOrWhiteSpace(request.Password)
                    ? null
                    : passwordHasher.HashPassword(request.Password)
                : null;

            if (IsPasswordProvider(request.Provider) && passwordHash is null)
                return Results.BadRequest(new { error = "Password is required for phone/email identity." });

            if (IsPasswordProvider(request.Provider))
            {
                if (string.IsNullOrWhiteSpace(request.VerificationCode))
                    return Results.BadRequest(new { error = "One-time verification code is required." });

                var verified = await repository.ConsumeVerificationCodeAsync(
                    principal.UserId.Value,
                    request.Provider,
                    identifier,
                    request.VerificationCode.Trim(),
                    cancellationToken);
                if (!verified)
                    return Results.Unauthorized();
            }

            try
            {
                await repository.LinkIdentityAsync(principal.UserId.Value, request.Provider, identifier, passwordHash, cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }

            var profile = await repository.GetProfileAsync(principal.UserId.Value, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapPost("/me/identities/unlink", async (
            HttpContext context,
            [FromBody] UnlinkIdentityRequest request,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var identifier = NormalizeIdentifier(request.Provider, request.Identifier);
            if (!IsIdentifierValid(request.Provider, identifier))
                return Results.BadRequest(new { error = "Identifier is invalid for selected provider." });

            try
            {
                await repository.RemoveIdentityAsync(
                    principal.UserId.Value,
                    request.Provider,
                    identifier,
                    cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            var profile = await repository.GetProfileAsync(principal.UserId.Value, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapPost("/me/identities/yandex/start", async (
            HttpContext context,
            [FromServices] MessengerRepository repository,
            [FromServices] YandexOAuthService yandexOAuth,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            try
            {
                var challenge = await yandexOAuth.StartDeviceCodeAsync(
                    $"staffmessenger-link-{principal.UserId.Value:N}-{Guid.NewGuid():N}",
                    "NewSunshine StaffMessenger",
                    cancellationToken);
                var challengeId = await repository.CreateYandexDeviceChallengeAsync(
                    principal.UserId.Value,
                    challenge.DeviceCode,
                    challenge.UserCode,
                    challenge.VerificationUrl,
                    challenge.ExpiresAt,
                    cancellationToken);

                return Results.Ok(new YandexQrStartResponse(
                    challengeId,
                    challenge.UserCode,
                    challenge.VerificationUrl,
                    challenge.VerificationUrl,
                    challenge.ExpiresAt,
                    challenge.IntervalSeconds));
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (HttpRequestException)
            {
                return Results.Problem("Yandex OAuth is unavailable.", statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapPost("/me/identities/yandex/complete", async (
            HttpContext context,
            [FromBody] YandexQrCompleteRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] YandexOAuthService yandexOAuth,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var challenge = await repository.GetYandexDeviceChallengeAsync(
                request.ChallengeId,
                principal.UserId.Value,
                cancellationToken);
            if (challenge is null)
                return Results.NotFound(new { error = "Yandex QR challenge was not found or expired." });

            string identifier;
            try
            {
                identifier = NormalizeIdentifier(
                    AuthProvider.YandexId,
                    await yandexOAuth.ResolveIdentifierFromDeviceCodeAsync(challenge.DeviceCode, cancellationToken));
            }
            catch (YandexAuthorizationPendingException)
            {
                return Results.Accepted(value: new { pending = true, message = "Yandex authorization is pending." });
            }
            catch (HttpRequestException)
            {
                return Results.Unauthorized();
            }
            catch (InvalidOperationException)
            {
                return Results.Unauthorized();
            }

            try
            {
                await repository.LinkIdentityAsync(
                    principal.UserId.Value,
                    AuthProvider.YandexId,
                    identifier,
                    null,
                    cancellationToken);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }

            await repository.CompleteYandexDeviceChallengeAsync(request.ChallengeId, cancellationToken);
            var profile = await repository.GetProfileAsync(principal.UserId.Value, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapPost("/verification/start", async (
            HttpContext context,
            [FromBody] StartIdentityVerificationRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] IVerificationCodeSender codeSender,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!IsPasswordProvider(request.Provider))
                return Results.BadRequest(new { error = "Verification code is supported for phone and email identities." });

            var identifier = NormalizeIdentifier(request.Provider, request.Identifier);
            if (!IsIdentifierValid(request.Provider, identifier))
                return Results.BadRequest(new { error = "Identifier is invalid for selected provider." });

            var principal = context.GetPrincipal();
            var code = Random.Shared.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            await repository.CreateVerificationCodeAsync(
                principal?.UserId,
                request.Provider,
                identifier,
                code,
                expiresAt,
                cancellationToken);

            try
            {
                await codeSender.SendAsync(request.Provider, identifier, code, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Results.Problem(
                    "Verification provider is unavailable.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var exposeCode = configuration.GetValue("Verification:ExposeDevelopmentCodes", false);
            return Results.Ok(new StartIdentityVerificationResponse(
                request.Provider,
                identifier,
                request.Provider == AuthProvider.Phone
                    ? "SMS code has been queued."
                    : "Email code has been queued.",
                expiresAt,
                exposeCode ? code : null));
        });

        group.MapPost("/me/2fa/setup", async (
            HttpContext context,
            [FromServices] MessengerRepository repository,
            [FromServices] TotpService totpService,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var profile = await repository.GetProfileAsync(principal.UserId.Value, cancellationToken);
            if (profile is null)
                return Results.NotFound();

            var phone = profile.Phone ?? profile.Identities.FirstOrDefault(identity => identity.Provider == AuthProvider.Phone)?.Identifier;
            if (string.IsNullOrWhiteSpace(phone))
                return Results.BadRequest(new { error = "Link a phone number before enabling 2FA." });

            var secret = totpService.CreateSecret();
            return Results.Ok(new TotpSetupResponse(
                secret,
                totpService.CreateOtpAuthUri("StaffMessenger", profile.Handle, secret),
                phone));
        });

        group.MapPost("/me/2fa/enable", async (
            HttpContext context,
            [FromBody] EnableTotpRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] TotpService totpService,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            if (!totpService.ValidateCode(request.Secret, request.Code))
                return Results.Unauthorized();

            await repository.EnableTotpAsync(principal.UserId.Value, request.Secret, request.Phone, cancellationToken);
            var profile = await repository.GetProfileAsync(principal.UserId.Value, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapPost("/me/2fa/disable", async (
            HttpContext context,
            [FromBody] DisableTotpRequest request,
            [FromServices] MessengerRepository repository,
            [FromServices] TotpService totpService,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            var totp = await repository.GetTotpAsync(principal.UserId.Value, cancellationToken);
            if (totp.Enabled && (totp.Secret is null || !totpService.ValidateCode(totp.Secret, request.Code)))
                return Results.Unauthorized();

            await repository.DisableTotpAsync(principal.UserId.Value, cancellationToken);
            var profile = await repository.GetProfileAsync(principal.UserId.Value, cancellationToken);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });

        group.MapGet("/users/search", async (
            [FromQuery] string q,
            HttpContext context,
            [FromServices] MessengerRepository repository,
            CancellationToken cancellationToken) =>
        {
            var principal = context.GetPrincipal();
            if (principal?.UserId is null)
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Results.Ok(Array.Empty<UserSearchResultDto>());

            var users = await repository.SearchUsersAsync(principal.UserId.Value, q, cancellationToken: cancellationToken);
            return Results.Ok(users);
        });

        return routes;
    }

    private static async Task<AuthResponse> CreateAuthResponseAsync(
        Guid userId,
        MessengerRepository repository,
        TokenService tokenService,
        HttpContext context,
        StaffMessenger.Contracts.Crypto.PublicDeviceKey? deviceKey,
        CancellationToken cancellationToken)
    {
        var profile = await repository.GetProfileAsync(userId, cancellationToken)
                      ?? throw new KeyNotFoundException("User profile was not found.");
        var token = tokenService.CreateUserToken();
        await repository.CreateSessionAsync(
            userId,
            token.Hash,
            token.ExpiresAt,
            BuildDeviceLabel(context, deviceKey),
            context.Request.Headers.UserAgent.ToString(),
            deviceKey?.KeyId,
            cancellationToken);
        return new AuthResponse(
            profile.Id,
            profile.Handle,
            profile.DisplayName,
            token.Token,
            token.ExpiresAt,
            profile.DeviceKeys);
    }

    private static async Task<AuthIdentityRecord> ResolveOrCreateYandexIdentityAsync(
        string identifier,
        string? displayName,
        StaffMessenger.Contracts.Crypto.PublicDeviceKey? deviceKey,
        MessengerRepository repository,
        CancellationToken cancellationToken)
    {
        var identity = await repository.FindIdentityAsync(AuthProvider.YandexId, identifier, cancellationToken);
        if (identity is not null)
            return identity;

        if (EmailRegex.IsMatch(identifier))
        {
            var emailIdentity = await repository.FindIdentityByVerifiedEmailAsync(identifier, cancellationToken);
            if (emailIdentity is not null)
            {
                await repository.LinkIdentityAsync(emailIdentity.UserId, AuthProvider.YandexId, identifier, null, cancellationToken);
                return emailIdentity;
            }
        }

        var effectiveDisplayName = string.IsNullOrWhiteSpace(displayName)
            ? BuildDisplayName(AuthProvider.YandexId, identifier)
            : displayName.Trim();
        var user = await CreateExternalUserWithUniqueHandleAsync(
            effectiveDisplayName,
            BuildInitialHandle(identifier),
            identifier,
            deviceKey,
            repository,
            cancellationToken);

        return new AuthIdentityRecord(user.Id, user.Handle, user.DisplayName, null, false, null);
    }

    private static async Task<UserRecord> CreateExternalUserWithUniqueHandleAsync(
        string displayName,
        string initialHandle,
        string identifier,
        StaffMessenger.Contracts.Crypto.PublicDeviceKey? deviceKey,
        MessengerRepository repository,
        CancellationToken cancellationToken)
    {
        var baseHandle = initialHandle.Trim().TrimStart('@');
        for (var index = 0; index < 1000; index++)
        {
            var handle = index == 0 ? baseHandle : $"{baseHandle}_{index + 1}";
            try
            {
                return await repository.CreateUserAsync(
                    new RegisterRequest(
                        displayName,
                        handle,
                        AuthProvider.YandexId,
                        identifier,
                        $"yandex-{Guid.NewGuid():N}",
                        deviceKey),
                    $"external:{AuthProvider.YandexId}",
                    cancellationToken);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
            {
            }
        }

        return await repository.CreateUserAsync(
            new RegisterRequest(
                displayName,
                $"user_{Guid.NewGuid():N}"[..13],
                AuthProvider.YandexId,
                identifier,
                $"yandex-{Guid.NewGuid():N}",
                deviceKey),
            $"external:{AuthProvider.YandexId}",
            cancellationToken);
    }

    private static bool ValidateTotpIfNeeded(AuthIdentityRecord identity, string? code, TotpService totpService)
    {
        return !identity.TwoFactorEnabled
               || (!string.IsNullOrWhiteSpace(identity.TotpSecret)
                   && !string.IsNullOrWhiteSpace(code)
                   && totpService.ValidateCode(identity.TotpSecret, code));
    }

    private static bool IsPasswordProvider(AuthProvider provider)
        => provider is AuthProvider.Email or AuthProvider.Phone;

    private static bool IsIdentifierValid(AuthProvider provider, string identifier)
    {
        return provider switch
        {
            AuthProvider.YandexId => !string.IsNullOrWhiteSpace(identifier),
            AuthProvider.Phone => E164PhoneRegex.IsMatch(identifier),
            AuthProvider.Email => EmailRegex.IsMatch(identifier),
            _ => false
        };
    }

    private static string NormalizeIdentifier(AuthProvider provider, string identifier)
    {
        var value = identifier.Trim();
        if (provider == AuthProvider.Phone)
        {
            value = value
                .Replace(" ", "", StringComparison.Ordinal)
                .Replace("-", "", StringComparison.Ordinal)
                .Replace("(", "", StringComparison.Ordinal)
                .Replace(")", "", StringComparison.Ordinal);

            if (value.StartsWith('8') && value.Length == 11)
                value = $"+7{value[1..]}";
        }

        return provider == AuthProvider.Email ? value.ToLowerInvariant() : value;
    }

    private static string BuildInitialHandle(string identifier)
    {
        var source = EmailRegex.IsMatch(identifier) ? identifier.Split('@')[0] : identifier;
        var handle = Regex.Replace(source.ToLowerInvariant(), "[^a-z0-9_]+", "_").Trim('_');
        return HandleRegex.IsMatch(handle) ? handle : $"user_{Guid.NewGuid():N}"[..13];
    }

    private static string BuildDisplayName(AuthProvider provider, string identifier)
    {
        return provider switch
        {
            AuthProvider.Phone => identifier,
            AuthProvider.Email => identifier.Split('@')[0],
            AuthProvider.YandexId when EmailRegex.IsMatch(identifier) => identifier.Split('@')[0],
            AuthProvider.YandexId => "YandexID user",
            _ => "StaffMessenger user"
        };
    }

    private static string BuildYandexAuthorizationUrl(IConfiguration configuration, Guid challengeId, string? redirectUri)
    {
        var clientId = configuration["Authentication:Yandex:ClientId"] ?? "<YANDEX_CLIENT_ID>";
        var redirect = Uri.EscapeDataString(redirectUri ?? configuration["Authentication:Yandex:RedirectUri"] ?? "staffmessenger://auth/yandex");
        return $"https://oauth.yandex.ru/authorize?response_type=code&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={redirect}&state={challengeId:D}";
    }

    private static string BuildDeviceLabel(
        HttpContext context,
        StaffMessenger.Contracts.Crypto.PublicDeviceKey? deviceKey)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
        var os = OperatingSystem.IsMacOS()
            ? "macOS"
            : OperatingSystem.IsWindows()
                ? "Windows"
                : OperatingSystem.IsLinux()
                    ? "Linux"
                    : "Desktop";
        var key = string.IsNullOrWhiteSpace(deviceKey?.KeyId)
            ? "без ключа"
            : deviceKey.KeyId.Length > 10 ? deviceKey.KeyId[..10] : deviceKey.KeyId;
        return string.IsNullOrWhiteSpace(userAgent)
            ? $"{os} Desktop, StaffMessenger ({key})"
            : $"{os} Desktop, StaffMessenger ({key})";
    }

    private static string? ExtractBearerToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..].Trim()
            : null;
    }
}
