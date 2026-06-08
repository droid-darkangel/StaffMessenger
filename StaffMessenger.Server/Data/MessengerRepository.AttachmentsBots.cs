using Npgsql;
using StaffMessenger.Contracts.Attachments;
using StaffMessenger.Contracts.Bots;
using StaffMessenger.Contracts.Crypto;
using StaffMessenger.Contracts.Messages;
using StaffMessenger.Crypto.Identity;
using StaffMessenger.Server.Services;

namespace StaffMessenger.Server.Data;

public sealed partial class MessengerRepository
{
    public async Task<AttachmentDto> SaveAttachmentAsync(
        Guid ownerUserId,
        StoredUpload upload,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into attachments (
                id,
                owner_user_id,
                kind,
                file_name,
                content_type,
                size_bytes,
                sha256_hex,
                storage_path,
                width,
                height,
                duration_ms,
                created_at)
            values (
                @id,
                @owner_user_id,
                @kind,
                @file_name,
                @content_type,
                @size_bytes,
                @sha256_hex,
                @storage_path,
                @width,
                @height,
                @duration_ms,
                now())
            returning id, kind, file_name, content_type, size_bytes, sha256_hex, width, height, duration_ms, created_at;
            """;

        var id = Guid.NewGuid();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("owner_user_id", ownerUserId);
        command.Parameters.AddWithValue("kind", upload.Kind.ToString());
        command.Parameters.AddWithValue("file_name", upload.FileName);
        command.Parameters.AddWithValue("content_type", upload.ContentType);
        command.Parameters.AddWithValue("size_bytes", upload.SizeBytes);
        command.Parameters.AddWithValue("sha256_hex", upload.Sha256Hex);
        command.Parameters.AddWithValue("storage_path", upload.StoragePath);
        command.Parameters.AddWithValue("width", DbValue(upload.Width));
        command.Parameters.AddWithValue("height", DbValue(upload.Height));
        command.Parameters.AddWithValue("duration_ms", DbValue(upload.DurationMs));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Attachment was not saved.");
        }

        return ReadAttachment(reader);
    }

    public async Task<StoredAttachment?> GetStoredAttachmentAsync(
        Guid attachmentId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, owner_user_id, storage_path, file_name, content_type, size_bytes
            from attachments
            where id = @id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", attachmentId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredAttachment(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5))
            : null;
    }

    public async Task<CreatedBotRecord> CreateBotAsync(
        Guid ownerUserId,
        CreateBotRequest request,
        string apiToken,
        string signingSecret,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        const string insertBot = """
            insert into bots (
                id,
                owner_user_id,
                name,
                description,
                webhook_url,
                permissions,
                signing_secret_hash,
                created_at)
            values (
                @id,
                @owner_user_id,
                @name,
                @description,
                @webhook_url,
                @permissions,
                @signing_secret_hash,
                now());
            """;

        const string insertToken = """
            insert into bot_tokens (id, bot_id, token_hash, expires_at)
            values (@id, @bot_id, @token_hash, @expires_at);
            """;

        var botId = Guid.NewGuid();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(insertBot, connection, tx))
        {
            command.Parameters.AddWithValue("id", botId);
            command.Parameters.AddWithValue("owner_user_id", ownerUserId);
            command.Parameters.AddWithValue("name", request.Name.Trim());
            command.Parameters.AddWithValue("description", request.Description.Trim());
            command.Parameters.AddWithValue("webhook_url", DbValue(request.WebhookUrl));
            command.Parameters.AddWithValue("permissions", (int)request.Permissions);
            command.Parameters.AddWithValue("signing_secret_hash", PasswordHasher.HashOpaqueToken(signingSecret));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand(insertToken, connection, tx))
        {
            command.Parameters.AddWithValue("id", Guid.NewGuid());
            command.Parameters.AddWithValue("bot_id", botId);
            command.Parameters.AddWithValue("token_hash", PasswordHasher.HashOpaqueToken(apiToken));
            command.Parameters.AddWithValue("expires_at", expiresAt.UtcDateTime);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new CreatedBotRecord(botId, request.Name.Trim(), apiToken, signingSecret, expiresAt, request.Permissions);
    }

    public async Task<IReadOnlyList<BotInfoDto>> GetBotsAsync(
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, name, description, webhook_url, permissions, created_at
            from bots
            where owner_user_id = @owner_user_id
            order by created_at desc;
            """;

        var bots = new List<BotInfoDto>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("owner_user_id", ownerUserId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            bots.Add(new BotInfoDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                (BotPermission)reader.GetInt32(4),
                ReadInstant(reader, 5)));
        }

        return bots;
    }

    public async Task JoinBotConversationAsync(
        Guid botId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            insert into bot_conversation_members (bot_id, conversation_id)
            values (@bot_id, @conversation_id)
            on conflict do nothing;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("bot_id", botId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> IsBotConversationMemberAsync(
        Guid botId,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select exists (
                select 1 from bot_conversation_members
                where bot_id = @bot_id and conversation_id = @conversation_id
            );
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("bot_id", botId);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<IReadOnlyList<BotConversationDto>> GetBotConversationsAsync(
        Guid botId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select c.id, coalesce(c.title, c.kind), c.kind, c.updated_at
            from bot_conversation_members bcm
            join conversations c on c.id = bcm.conversation_id
            where bcm.bot_id = @bot_id
            order by c.updated_at desc;
            """;

        var conversations = new List<BotConversationDto>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("bot_id", botId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conversations.Add(new BotConversationDto(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                ReadInstant(reader, 3)));
        }

        return conversations;
    }

    public async Task<IReadOnlyList<MessageDto>> GetBotMessagesAsync(
        Guid botId,
        Guid conversationId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!await IsBotConversationMemberAsync(botId, conversationId, cancellationToken))
        {
            throw new UnauthorizedAccessException("Bot is not a member of the conversation.");
        }

        const string sql = """
            select m.id,
                   m.conversation_id,
                   m.sender_user_id,
                   m.sender_bot_id,
                   coalesce(u.display_name, b.name, 'System') as sender_name,
                   case when m.deleted_for_everyone_at is null then m.plain_preview else 'Сообщение удалено' end as plain_preview,
                   m.encrypted_body::text,
                   m.sent_at,
                   case when m.deleted_for_everyone_at is null then m.state else 'Deleted' end as state,
                   m.reply_to_message_id
            from messages m
            left join users u on u.id = m.sender_user_id
            left join bots b on b.id = m.sender_bot_id
            where m.conversation_id = @conversation_id
            order by m.sent_at desc
            limit @limit;
            """;

        var messages = new List<MessageDto>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            messages.Add(ReadMessage(reader, []));

        await reader.DisposeAsync();

        var hydrated = new List<MessageDto>(messages.Count);
        foreach (var message in messages)
        {
            var attachments = await GetAttachmentsForMessageAsync(connection, message.Id, cancellationToken);
            hydrated.Add(message with { Attachments = attachments });
        }

        hydrated.Reverse();
        return hydrated;
    }

    public static CryptoEnvelope CreateBotPlainTextEnvelope(string text)
    {
        var payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        return new CryptoEnvelope(
            "BOT-PLAINTEXT-BRIDGE",
            "bot",
            "",
            payload,
            "",
            "",
            "");
    }
}
