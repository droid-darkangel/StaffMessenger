using Npgsql;
using NpgsqlTypes;
using StaffMessenger.Contracts.Auth;
using StaffMessenger.Contracts.Crypto;
using StaffMessenger.Crypto.Identity;

namespace StaffMessenger.Server.Data;

public sealed partial class MessengerRepository
{
    public async Task<UserRecord> CreateUserAsync(
        RegisterRequest request,
        string passwordHash,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var handle = NormalizeHandle(request.Handle);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        const string insertUser = """
            insert into users (id, handle, display_name, password_hash, status)
            values (@id, @handle, @display_name, @password_hash, 'online');
            """;

        await using (var command = new NpgsqlCommand(insertUser, connection, tx))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("handle", handle);
            command.Parameters.AddWithValue("display_name", request.DisplayName.Trim());
            command.Parameters.AddWithValue("password_hash", passwordHash);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (request.DeviceKey is not null)
            await UpsertDeviceKeyAsync(connection, tx, id, request.DeviceKey, cancellationToken);

        await InsertAuthIdentityAsync(
            connection,
            tx,
            id,
            request.Provider,
            NormalizeIdentifier(request.Provider, request.Identifier),
            passwordHash,
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return new UserRecord(id, handle, request.DisplayName.Trim(), passwordHash);
    }

    public async Task<UserRecord?> FindUserByHandleAsync(string handle, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, handle, display_name, password_hash
            from users
            where lower(handle) = @handle
            limit 1;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("handle", NormalizeHandle(handle));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new UserRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    public async Task<AuthIdentityRecord?> FindIdentityAsync(
        AuthProvider provider,
        string identifier,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select u.id, u.handle, u.display_name, ai.password_hash, u.two_factor_enabled, u.totp_secret
            from auth_identities ai
            join users u on u.id = ai.user_id
            where ai.provider = @provider and lower(ai.identifier) = @identifier
            limit 1;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("provider", provider.ToString());
        command.Parameters.AddWithValue("identifier", NormalizeIdentifier(provider, identifier).ToLowerInvariant());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AuthIdentityRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetString(5))
            : null;
    }

    public async Task<AuthIdentityRecord?> FindIdentityByVerifiedEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
        => await FindIdentityAsync(AuthProvider.Email, email, cancellationToken);

    public async Task LinkIdentityAsync(
        Guid userId,
        AuthProvider provider,
        string identifier,
        string? passwordHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await InsertAuthIdentityAsync(
            connection,
            tx,
            userId,
            provider,
            NormalizeIdentifier(provider, identifier),
            passwordHash,
            cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task RemoveIdentityAsync(
        Guid userId,
        AuthProvider provider,
        string identifier,
        CancellationToken cancellationToken = default)
    {
        const string countSql = """
            select count(*)
            from auth_identities
            where user_id = @user_id;
            """;

        const string deleteSql = """
            delete from auth_identities
            where user_id = @user_id
              and provider = @provider
              and lower(identifier) = @identifier;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var count = new NpgsqlCommand(countSql, connection, tx))
        {
            count.Parameters.AddWithValue("user_id", userId);
            var linkedCount = (long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L);
            if (linkedCount <= 1)
                throw new InvalidOperationException("At least one login method must stay linked.");
        }

        await using (var delete = new NpgsqlCommand(deleteSql, connection, tx))
        {
            delete.Parameters.AddWithValue("user_id", userId);
            delete.Parameters.AddWithValue("provider", provider.ToString());
            delete.Parameters.AddWithValue("identifier", NormalizeIdentifier(provider, identifier).ToLowerInvariant());
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task CreateVerificationCodeAsync(
        Guid? userId,
        AuthProvider provider,
        string identifier,
        string code,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into identity_verification_codes (
                id, user_id, provider, identifier, code_hash, expires_at)
            values (
                @id, @user_id, @provider, @identifier, @code_hash, @expires_at);
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.Add("user_id", NpgsqlDbType.Uuid).Value = userId.HasValue ? userId.Value : DBNull.Value;
        command.Parameters.AddWithValue("provider", provider.ToString());
        command.Parameters.AddWithValue("identifier", NormalizeIdentifier(provider, identifier));
        command.Parameters.AddWithValue("code_hash", PasswordHasher.HashOpaqueToken(code));
        command.Parameters.AddWithValue("expires_at", expiresAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> ConsumeVerificationCodeAsync(
        Guid? userId,
        AuthProvider provider,
        string identifier,
        string code,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update identity_verification_codes
            set consumed_at = now()
            where id = (
                select id
                from identity_verification_codes
                where provider = @provider
                  and lower(identifier) = @identifier
                  and code_hash = @code_hash
                  and expires_at > now()
                  and consumed_at is null
                  and (@user_id is null or user_id is null or user_id = @user_id)
                order by created_at desc
                limit 1
            )
            returning id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("provider", provider.ToString());
        command.Parameters.AddWithValue("identifier", NormalizeIdentifier(provider, identifier).ToLowerInvariant());
        command.Parameters.AddWithValue("code_hash", PasswordHasher.HashOpaqueToken(code));
        command.Parameters.Add("user_id", NpgsqlDbType.Uuid).Value = userId.HasValue ? userId.Value : DBNull.Value;
        return await command.ExecuteScalarAsync(cancellationToken) is Guid;
    }

    public async Task<IReadOnlyList<PublicDeviceKey>> GetDeviceKeysAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await GetDeviceKeysAsync(connection, userId, cancellationToken);
    }

    private static async Task<IReadOnlyList<PublicDeviceKey>> GetDeviceKeysAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select algorithm, key_id, public_key_base64, created_at
            from device_keys
            where user_id = @user_id
            order by created_at desc;
            """;

        var keys = new List<PublicDeviceKey>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new PublicDeviceKey(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                ReadInstant(reader, 3)));
        }

        return keys;
    }

    public async Task UpsertDeviceKeyAsync(
        Guid userId,
        PublicDeviceKey deviceKey,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await UpsertDeviceKeyAsync(connection, tx, userId, deviceKey, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task CreateSessionAsync(
        Guid userId,
        string tokenHash,
        DateTimeOffset expiresAt,
        string deviceLabel,
        string userAgent,
        string? deviceKeyId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into sessions (
                id, user_id, token_hash, device_key_id, device_label, user_agent, expires_at, last_seen_at)
            values (
                @id, @user_id, @token_hash, @device_key_id, @device_label, @user_agent, @expires_at, now());
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("device_key_id", DbValue(deviceKeyId));
        command.Parameters.AddWithValue("device_label", deviceLabel);
        command.Parameters.AddWithValue("user_agent", userAgent);
        command.Parameters.AddWithValue("expires_at", expiresAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeSessionAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        const string sql = """
            update sessions
            set revoked_at = now()
            where token_hash = @token_hash and revoked_at is null;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("token_hash", tokenHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PrincipalRecord?> ResolvePrincipalAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select s.id as session_id, s.user_id, null::uuid as bot_id, u.display_name, u.handle, false as is_bot, s.expires_at
            from sessions s
            join users u on u.id = s.user_id
            where s.token_hash = @token_hash and s.revoked_at is null and s.expires_at > now()
            union all
            select null::uuid as session_id, null::uuid as user_id, b.id as bot_id, b.name as display_name, ('bot:' || b.name) as handle, true as is_bot, bt.expires_at
            from bot_tokens bt
            join bots b on b.id = bt.bot_id
            where bt.token_hash = @token_hash and bt.revoked_at is null and bt.expires_at > now()
            limit 1;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("token_hash", tokenHash);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        return new PrincipalRecord(
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetBoolean(5),
            ReadInstant(reader, 6));
    }

    public async Task TouchSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            update sessions
            set last_seen_at = now()
            where id = @id and revoked_at is null and expires_at > now();
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AuthSessionDto>> GetSessionsAsync(
        Guid userId,
        Guid? currentSessionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id,
                   coalesce(nullif(device_label, ''), 'Desktop client') as device_label,
                   coalesce(user_agent, '') as user_agent,
                   created_at,
                   last_seen_at,
                   expires_at
            from sessions
            where user_id = @user_id
              and revoked_at is null
              and expires_at > now()
            order by last_seen_at desc, created_at desc;
            """;

        var sessions = new List<AuthSessionDto>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            sessions.Add(new AuthSessionDto(
                id,
                reader.GetString(1),
                reader.GetString(2),
                ReadInstant(reader, 3),
                ReadInstant(reader, 4),
                ReadInstant(reader, 5),
                currentSessionId.HasValue && id == currentSessionId.Value));
        }

        return sessions;
    }

    public async Task RevokeSessionByIdAsync(
        Guid userId,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update sessions
            set revoked_at = now()
            where user_id = @user_id and id = @session_id and revoked_at is null;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("session_id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RevokeOtherSessionsAsync(
        Guid userId,
        Guid currentSessionId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update sessions
            set revoked_at = now()
            where user_id = @user_id
              and id <> @current_session_id
              and revoked_at is null;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("current_session_id", currentSessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<UserProfileDto?> GetProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await GetProfileAsync(connection, userId, cancellationToken);
    }

    private static async Task<UserProfileDto?> GetProfileAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id, handle, display_name, avatar_url, about, birth_date, phone, status,
                   two_factor_enabled,
                   allow_profile_search,
                   allow_phone_discovery,
                   show_birthday,
                   show_last_seen,
                   send_read_receipts,
                   encrypt_media_metadata,
                   show_message_notifications,
                   play_incoming_sound,
                   show_text_preview
            from users
            where id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var profile = new UserProfileDto(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetString(7),
            reader.GetBoolean(8),
            [],
            new UserPrivacySettingsDto(
                reader.GetBoolean(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12),
                reader.GetBoolean(13),
                reader.GetBoolean(14)),
            new UserNotificationSettingsDto(
                reader.GetBoolean(15),
                reader.GetBoolean(16),
                reader.GetBoolean(17)),
            []);

        await reader.DisposeAsync();
        var keys = await GetDeviceKeysAsync(connection, userId, cancellationToken);
        var identities = await GetLinkedIdentitiesAsync(connection, userId, cancellationToken);
        return profile with { DeviceKeys = keys, Identities = identities };
    }

    public async Task<UserProfileDto?> UpdateProfileAsync(
        Guid userId,
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update users
            set handle = @handle,
                display_name = @display_name,
                avatar_url = @avatar_url,
                about = @about,
                birth_date = @birth_date,
                phone = @phone
            where id = @id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("handle", NormalizeHandle(request.Handle));
        command.Parameters.AddWithValue("display_name", request.DisplayName.Trim());
        command.Parameters.AddWithValue("avatar_url", DbValue(request.AvatarUrl));
        command.Parameters.AddWithValue("about", DbValue(request.About));
        command.Parameters.AddWithValue("birth_date", DbValue(request.BirthDate?.ToDateTime(TimeOnly.MinValue)));
        command.Parameters.AddWithValue("phone", DbValue(request.Phone));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetProfileAsync(connection, userId, cancellationToken);
    }

    public async Task<UserProfileDto?> UpdateSettingsAsync(
        Guid userId,
        UpdateUserSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update users
            set allow_profile_search = @allow_profile_search,
                allow_phone_discovery = @allow_phone_discovery,
                show_birthday = @show_birthday,
                show_last_seen = @show_last_seen,
                send_read_receipts = @send_read_receipts,
                encrypt_media_metadata = @encrypt_media_metadata,
                show_message_notifications = @show_message_notifications,
                play_incoming_sound = @play_incoming_sound,
                show_text_preview = @show_text_preview
            where id = @id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("allow_profile_search", request.Privacy.AllowProfileSearch);
        command.Parameters.AddWithValue("allow_phone_discovery", request.Privacy.AllowPhoneDiscovery);
        command.Parameters.AddWithValue("show_birthday", request.Privacy.ShowBirthday);
        command.Parameters.AddWithValue("show_last_seen", request.Privacy.ShowLastSeen);
        command.Parameters.AddWithValue("send_read_receipts", request.Privacy.SendReadReceipts);
        command.Parameters.AddWithValue("encrypt_media_metadata", request.Privacy.EncryptMediaMetadata);
        command.Parameters.AddWithValue("show_message_notifications", request.Notifications.ShowMessageNotifications);
        command.Parameters.AddWithValue("play_incoming_sound", request.Notifications.PlayIncomingSound);
        command.Parameters.AddWithValue("show_text_preview", request.Notifications.ShowTextPreview);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetProfileAsync(connection, userId, cancellationToken);
    }

    public async Task EnableTotpAsync(
        Guid userId,
        string secret,
        string phone,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update users
            set two_factor_enabled = true,
                totp_secret = @totp_secret,
                totp_phone = @totp_phone
            where id = @id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("totp_secret", secret);
        command.Parameters.AddWithValue("totp_phone", NormalizeIdentifier(AuthProvider.Phone, phone));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DisableTotpAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            update users
            set two_factor_enabled = false,
                totp_secret = null,
                totp_phone = null
            where id = @id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(bool Enabled, string? Secret, string? Phone)> GetTotpAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select two_factor_enabled, totp_secret, totp_phone
            from users
            where id = @id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetBoolean(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2))
            : (false, null, null);
    }

    public async Task<Guid> CreateYandexDeviceChallengeAsync(
        Guid? userId,
        string deviceCode,
        string userCode,
        string verificationUrl,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into yandex_device_challenges (
                id, user_id, device_code, user_code, verification_url, expires_at)
            values (
                @id, @user_id, @device_code, @user_code, @verification_url, @expires_at);
            """;

        var id = Guid.NewGuid();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add("user_id", NpgsqlDbType.Uuid).Value = userId.HasValue ? userId.Value : DBNull.Value;
        command.Parameters.AddWithValue("device_code", deviceCode);
        command.Parameters.AddWithValue("user_code", userCode);
        command.Parameters.AddWithValue("verification_url", verificationUrl);
        command.Parameters.AddWithValue("expires_at", expiresAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<YandexDeviceChallengeRecord?> GetYandexDeviceChallengeAsync(
        Guid challengeId,
        Guid? userId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, user_id, device_code, user_code, verification_url, expires_at
            from yandex_device_challenges
            where id = @id
              and consumed_at is null
              and expires_at > now()
              and ((@user_id is null and user_id is null) or user_id = @user_id)
            limit 1;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", challengeId);
        command.Parameters.Add("user_id", NpgsqlDbType.Uuid).Value = userId.HasValue ? userId.Value : DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new YandexDeviceChallengeRecord(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ReadInstant(reader, 5))
            : null;
    }

    public async Task CompleteYandexDeviceChallengeAsync(
        Guid challengeId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            update yandex_device_challenges
            set consumed_at = now()
            where id = @id and consumed_at is null;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", challengeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(
        Guid requesterId,
        string query,
        int limit = 8,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, handle, display_name, avatar_url, about, status
            from users
            where id <> @requester_id
              and (
                (
                  allow_profile_search = true
                  and (
                    lower(handle) like @query
                    or lower(display_name) like @query
                  )
                )
                or (
                  allow_phone_discovery = true
                  and @phone_query is not null
                  and phone = @phone_query
                )
              )
            order by
              case when lower(handle) = @exact then 0 else 1 end,
              display_name
            limit @limit;
            """;

        var normalized = query.Trim().TrimStart('@').ToLowerInvariant();
        var phoneQuery = NormalizeIdentifier(AuthProvider.Phone, query);
        var hasPhoneQuery = phoneQuery.StartsWith('+') && phoneQuery.Length >= 8;
        var users = new List<UserSearchResultDto>();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("requester_id", requesterId);
        command.Parameters.AddWithValue("query", $"%{normalized}%");
        command.Parameters.Add("phone_query", NpgsqlDbType.Text).Value = hasPhoneQuery ? phoneQuery : DBNull.Value;
        command.Parameters.AddWithValue("exact", normalized);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 20));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new UserSearchResultDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5).Equals("online", StringComparison.OrdinalIgnoreCase)));
        }

        return users;
    }

    private static async Task UpsertDeviceKeyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid userId,
        PublicDeviceKey deviceKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into device_keys (id, user_id, algorithm, key_id, public_key_base64, created_at, last_seen_at)
            values (@id, @user_id, @algorithm, @key_id, @public_key_base64, @created_at, now())
            on conflict (user_id, key_id)
            do update set public_key_base64 = excluded.public_key_base64, last_seen_at = now();
            """;

        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("algorithm", deviceKey.Algorithm);
        command.Parameters.AddWithValue("key_id", deviceKey.KeyId);
        command.Parameters.AddWithValue("public_key_base64", deviceKey.PublicKeyBase64);
        command.Parameters.AddWithValue("created_at", deviceKey.CreatedAt.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<AuthIdentityDto>> GetLinkedIdentitiesAsync(
        NpgsqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select provider, identifier, verified_at, created_at
            from auth_identities
            where user_id = @user_id
            order by created_at;
            """;

        var identities = new List<AuthIdentityDto>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var provider = Enum.TryParse<AuthProvider>(reader.GetString(0), out var parsed)
                ? parsed
                : AuthProvider.Email;
            identities.Add(new AuthIdentityDto(
                provider,
                reader.GetString(1),
                reader.IsDBNull(2) ? AuthIdentityStatus.Pending : AuthIdentityStatus.Verified,
                ReadInstant(reader, 3)));
        }

        return identities;
    }

    private static async Task InsertAuthIdentityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid userId,
        AuthProvider provider,
        string identifier,
        string? passwordHash,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into auth_identities (id, user_id, provider, identifier, password_hash, verified_at)
            values (@id, @user_id, @provider, @identifier, @password_hash, now())
            on conflict (provider, lower(identifier)) do nothing
            returning id;
            """;

        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("provider", provider.ToString());
        command.Parameters.AddWithValue("identifier", identifier);
        command.Parameters.AddWithValue("password_hash", DbValue(passwordHash));
        var inserted = await command.ExecuteScalarAsync(cancellationToken);
        if (inserted is Guid)
            return;

        const string existingSql = """
            select user_id
            from auth_identities
            where provider = @provider and lower(identifier) = @identifier
            limit 1;
            """;

        await using var existing = new NpgsqlCommand(existingSql, connection, tx);
        existing.Parameters.AddWithValue("provider", provider.ToString());
        existing.Parameters.AddWithValue("identifier", identifier.ToLowerInvariant());
        var existingUserId = await existing.ExecuteScalarAsync(cancellationToken);
        if (existingUserId is not Guid linkedUserId || linkedUserId != userId)
            throw new InvalidOperationException("Identity is already linked to another profile.");

        const string updateSql = """
            update auth_identities
            set password_hash = coalesce(@password_hash, password_hash),
                verified_at = now()
            where user_id = @user_id
              and provider = @provider
              and lower(identifier) = @identifier;
            """;

        await using var update = new NpgsqlCommand(updateSql, connection, tx);
        update.Parameters.AddWithValue("user_id", userId);
        update.Parameters.AddWithValue("provider", provider.ToString());
        update.Parameters.AddWithValue("identifier", identifier.ToLowerInvariant());
        update.Parameters.AddWithValue("password_hash", DbValue(passwordHash));
        await update.ExecuteNonQueryAsync(cancellationToken);
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
}
