using System.Text.Json;
using Npgsql;
using StaffMessenger.Contracts.Attachments;
using StaffMessenger.Contracts.Crypto;
using StaffMessenger.Contracts.Messages;

namespace StaffMessenger.Server.Data;

public sealed partial class MessengerRepository
{
    public async Task<MessageDto> SaveUserMessageAsync(
        Guid userId,
        Guid conversationId,
        SendMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!await IsConversationMemberAsync(conversationId, userId, cancellationToken))
            throw new UnauthorizedAccessException("User is not a member of the conversation.");

        return await SaveMessageCoreAsync(
            conversationId,
            userId,
            null,
            request.ClientMessageId,
            request.PlainPreview,
            request.Body,
            request.AttachmentIds,
            request.ReplyToMessageId,
            cancellationToken);
    }

    public async Task<MessageDto> SaveBotMessageAsync(
        Guid botId,
        Guid conversationId,
        string plainPreview,
        CryptoEnvelope body,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default)
    {
        if (!await IsBotConversationMemberAsync(botId, conversationId, cancellationToken))
            throw new UnauthorizedAccessException("Bot is not a member of the conversation.");

        return await SaveMessageCoreAsync(
            conversationId,
            null,
            botId,
            Guid.NewGuid(),
            plainPreview,
            body,
            attachmentIds,
            null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<MessageDto>> GetMessagesAsync(
        Guid userId,
        Guid conversationId,
        int limit = 80,
        DateTimeOffset? before = null,
        CancellationToken cancellationToken = default)
    {
        if (!await IsConversationMemberAsync(conversationId, userId, cancellationToken))
        {
            throw new UnauthorizedAccessException("User is not a member of the conversation.");
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
              and (@before::timestamptz is null or m.sent_at < @before::timestamptz)
              and not exists (
                  select 1 from message_deletions md
                  where md.message_id = m.id and md.user_id = @user_id
              )
            order by m.sent_at desc
            limit @limit;
            """;

        var messages = new List<MessageDto>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("before", DbValue(before?.UtcDateTime));
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(ReadMessage(reader, []));
        }

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

    public async Task DeleteMessageAsync(
        Guid userId,
        Guid conversationId,
        Guid messageId,
        DeleteMessageScope scope,
        CancellationToken cancellationToken = default)
    {
        if (!await IsConversationMemberAsync(conversationId, userId, cancellationToken))
        {
            throw new UnauthorizedAccessException("User is not a member of the conversation.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        if (scope == DeleteMessageScope.ForEveryone)
        {
            const string sql = """
                update messages
                set deleted_for_everyone_at = now(),
                    state = 'Deleted',
                    plain_preview = 'Сообщение удалено'
                where id = @message_id
                  and conversation_id = @conversation_id
                  and sender_user_id = @user_id;
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("message_id", messageId);
            command.Parameters.AddWithValue("conversation_id", conversationId);
            command.Parameters.AddWithValue("user_id", userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        const string localSql = """
            insert into message_deletions (message_id, user_id)
            values (@message_id, @user_id)
            on conflict do nothing;
            """;

        await using var localCommand = new NpgsqlCommand(localSql, connection);
        localCommand.Parameters.AddWithValue("message_id", messageId);
        localCommand.Parameters.AddWithValue("user_id", userId);
        await localCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<MessageDto> SaveMessageCoreAsync(
        Guid conversationId,
        Guid? senderUserId,
        Guid? senderBotId,
        Guid clientMessageId,
        string plainPreview,
        CryptoEnvelope body,
        IReadOnlyList<Guid> attachmentIds,
        Guid? replyToMessageId,
        CancellationToken cancellationToken)
    {
        const string insertMessage = """
            insert into messages (
                id,
                conversation_id,
                sender_user_id,
                sender_bot_id,
                client_message_id,
                plain_preview,
                encrypted_body,
                state,
                reply_to_message_id,
                sent_at)
            values (
                @id,
                @conversation_id,
                @sender_user_id,
                @sender_bot_id,
                @client_message_id,
                @plain_preview,
                @encrypted_body::jsonb,
                @state,
                @reply_to_message_id,
                now());

            update conversations
            set updated_at = now()
            where id = @conversation_id;
            """;

        var messageId = Guid.NewGuid();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(insertMessage, connection, tx))
        {
            command.Parameters.AddWithValue("id", messageId);
            command.Parameters.AddWithValue("conversation_id", conversationId);
            command.Parameters.AddWithValue("sender_user_id", DbValue(senderUserId));
            command.Parameters.AddWithValue("sender_bot_id", DbValue(senderBotId));
            command.Parameters.AddWithValue("client_message_id", clientMessageId);
            command.Parameters.AddWithValue("plain_preview", plainPreview);
            command.Parameters.AddWithValue("encrypted_body", JsonSerializer.Serialize(body, JsonOptions));
            command.Parameters.AddWithValue("state", MessageDeliveryState.Sent.ToString());
            command.Parameters.AddWithValue("reply_to_message_id", DbValue(replyToMessageId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var attachmentId in attachmentIds.Distinct())
        {
            await using var command = new NpgsqlCommand(
                "insert into message_attachments (message_id, attachment_id) values (@message_id, @attachment_id) on conflict do nothing;",
                connection,
                tx);
            command.Parameters.AddWithValue("message_id", messageId);
            command.Parameters.AddWithValue("attachment_id", attachmentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        var loaded = await GetMessageByIdAsync(connection, messageId, cancellationToken);
        return loaded ?? throw new InvalidOperationException("Created message could not be loaded.");
    }

    private async Task<MessageDto?> GetMessageByIdAsync(
        NpgsqlConnection connection,
        Guid messageId,
        CancellationToken cancellationToken)
    {
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
            where m.id = @id;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", messageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var message = ReadMessage(reader, []);
        await reader.DisposeAsync();
        var attachments = await GetAttachmentsForMessageAsync(connection, message.Id, cancellationToken);
        return message with { Attachments = attachments };
    }

    private async Task<IReadOnlyList<AttachmentDto>> GetAttachmentsForMessageAsync(
        NpgsqlConnection connection,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select a.id,
                   a.kind,
                   a.file_name,
                   a.content_type,
                   a.size_bytes,
                   a.sha256_hex,
                   a.width,
                   a.height,
                   a.duration_ms,
                   a.created_at
            from message_attachments ma
            join attachments a on a.id = ma.attachment_id
            where ma.message_id = @message_id
            order by a.created_at;
            """;

        var attachments = new List<AttachmentDto>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("message_id", messageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            attachments.Add(ReadAttachment(reader));

        return attachments;
    }

    private static MessageDto ReadMessage(NpgsqlDataReader reader, IReadOnlyList<AttachmentDto> attachments)
    {
        var stateText = reader.GetString(8);
        var state = Enum.TryParse<MessageDeliveryState>(stateText, true, out var parsedState)
            ? parsedState
            : MessageDeliveryState.Sent;

        return new MessageDto(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            ReadEnvelope(reader.GetString(6)),
            attachments,
            ReadInstant(reader, 7),
            state,
            reader.IsDBNull(9) ? null : reader.GetGuid(9));
    }

    private static AttachmentDto ReadAttachment(NpgsqlDataReader reader)
    {
        var id = reader.GetGuid(0);
        return new AttachmentDto(
            id,
            ReadAttachmentKind(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            $"/api/attachments/{id}/download",
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            ReadInstant(reader, 9));
    }
}
