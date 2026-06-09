using Npgsql;
using StaffMessenger.Contracts.Conversations;
using StaffMessenger.Contracts.Crypto;
using StaffMessenger.Contracts.Messages;

namespace StaffMessenger.Server.Data;

public sealed partial class MessengerRepository
{
    public async Task<IReadOnlyList<ConversationSummary>> GetConversationsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select c.id,
                   c.kind,
                   coalesce(c.title, '') as title,
                   c.avatar_url,
                   c.updated_at,
                   coalesce((
                       select m.plain_preview
                       from messages m
                       where m.conversation_id = c.id
                         and m.deleted_for_everyone_at is null
                         and not exists (
                             select 1 from message_deletions md
                             where md.message_id = m.id and md.user_id = @user_id
                         )
                       order by m.sent_at desc
                       limit 1
                   ), 'Защищенный диалог') as last_preview,
                   (
                       select count(*)
                       from messages m
                       where m.conversation_id = c.id
                         and (cm.last_read_at is null or m.sent_at > cm.last_read_at)
                         and m.sent_at >= cm.joined_at
                         and m.deleted_for_everyone_at is null
                         and (m.sender_user_id is null or m.sender_user_id <> @user_id)
                         and not exists (
                             select 1 from message_deletions md
                             where md.message_id = m.id and md.user_id = @user_id
                         )
                   )::int as unread_count
            from conversations c
            join conversation_members cm on cm.conversation_id = c.id
            where cm.user_id = @user_id
              and not exists (
                  select 1 from conversation_deletions cd
                  where cd.conversation_id = c.id and cd.user_id = @user_id
              )
            order by c.updated_at desc;
            """;

        var conversations = new List<ConversationSummary>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            conversations.Add(new ConversationSummary(
                reader.GetGuid(0),
                ReadConversationKind(reader.GetString(1)),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(5),
                reader.GetInt32(6),
                ReadInstant(reader, 4),
                []));
        }

        await reader.DisposeAsync();

        var hydrated = new List<ConversationSummary>(conversations.Count);
        foreach (var conversation in conversations)
        {
            var members = await GetMembersAsync(connection, conversation.Id, cancellationToken);
            var title = conversation.Title;

            if (conversation.Kind == ConversationKind.Saved)
                title = "Сохраненные сообщения";
            else if (conversation.Kind == ConversationKind.Announcement)
                title = "NewSunshine";
            else if (conversation.Kind == ConversationKind.Direct && string.IsNullOrWhiteSpace(title))
                title = members.FirstOrDefault(member => member.UserId != userId)?.DisplayName
                        ?? members.FirstOrDefault()?.DisplayName
                        ?? "Direct";

            hydrated.Add(conversation with { Title = title, Members = members });
        }

        return hydrated;
    }

    public async Task<ConversationSummary?> GetConversationAsync(
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await GetConversationAsync(connection, conversationId, userId, cancellationToken);
    }

    private async Task<ConversationSummary?> GetConversationAsync(
        NpgsqlConnection connection,
        Guid conversationId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select c.id,
                   c.kind,
                   coalesce(c.title, '') as title,
                   c.avatar_url,
                   c.updated_at,
                   coalesce((
                       select m.plain_preview
                       from messages m
                       where m.conversation_id = c.id
                         and m.deleted_for_everyone_at is null
                         and not exists (
                             select 1 from message_deletions md
                             where md.message_id = m.id and md.user_id = @user_id
                         )
                       order by m.sent_at desc
                       limit 1
                   ), 'Защищенный диалог') as last_preview,
                   (
                       select count(*)
                       from messages m
                       where m.conversation_id = c.id
                         and (cm.last_read_at is null or m.sent_at > cm.last_read_at)
                         and m.sent_at >= cm.joined_at
                         and m.deleted_for_everyone_at is null
                         and (m.sender_user_id is null or m.sender_user_id <> @user_id)
                         and not exists (
                             select 1 from message_deletions md
                             where md.message_id = m.id and md.user_id = @user_id
                         )
                   )::int as unread_count
            from conversations c
            join conversation_members cm on cm.conversation_id = c.id and cm.user_id = @user_id
            where c.id = @conversation_id
              and not exists (
                  select 1 from conversation_deletions cd
                  where cd.conversation_id = c.id and cd.user_id = @user_id
              )
            limit 1;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("user_id", userId);

        ConversationSummary? conversation = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                conversation = new ConversationSummary(
                    reader.GetGuid(0),
                    ReadConversationKind(reader.GetString(1)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    ReadInstant(reader, 4),
                    []);
            }
        }

        if (conversation is null)
            return null;

        var members = await GetMembersAsync(connection, conversation.Id, cancellationToken);
        var title = conversation.Title;

        if (conversation.Kind == ConversationKind.Saved)
            title = "Сохраненные сообщения";
        else if (conversation.Kind == ConversationKind.Announcement)
            title = "NewSunshine";
        else if (conversation.Kind == ConversationKind.Direct && string.IsNullOrWhiteSpace(title))
            title = members.FirstOrDefault(member => member.UserId != userId)?.DisplayName
                    ?? members.FirstOrDefault()?.DisplayName
                    ?? "Direct";

        return conversation with { Title = title, Members = members };
    }

    public async Task<ConversationSummary> CreateDirectConversationAsync(
        Guid userId,
        string peerHandle,
        CancellationToken cancellationToken = default)
    {
        var peer = await FindUserByHandleAsync(peerHandle, cancellationToken)
                   ?? throw new KeyNotFoundException($"User @{NormalizeHandle(peerHandle)} was not found.");

        if (peer.Id == userId)
            return await GetOrCreateSavedConversationAsync(userId, cancellationToken);

        const string findExisting = """
            select c.id
            from conversations c
            join conversation_members self_member on self_member.conversation_id = c.id and self_member.user_id = @user_id
            join conversation_members peer_member on peer_member.conversation_id = c.id and peer_member.user_id = @peer_id
            where c.kind = 'Direct'
            limit 1;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var find = new NpgsqlCommand(findExisting, connection))
        {
            find.Parameters.AddWithValue("user_id", userId);
            find.Parameters.AddWithValue("peer_id", peer.Id);
            var existing = await find.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid existingId)
                return await GetConversationAsync(connection, existingId, userId, cancellationToken)
                       ?? throw new InvalidOperationException("Existing conversation could not be loaded.");
        }

        var conversationId = Guid.NewGuid();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await InsertConversationAsync(connection, tx, conversationId, "Direct", null, cancellationToken);
        await InsertMemberAsync(connection, tx, conversationId, userId, "owner", cancellationToken);
        await InsertMemberAsync(connection, tx, conversationId, peer.Id, "member", cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return await GetConversationAsync(connection, conversationId, userId, cancellationToken)
               ?? throw new InvalidOperationException("Created conversation could not be loaded.");
    }

    public async Task<ConversationSummary> GetOrCreateSavedConversationAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        const string findExisting = """
            select c.id
            from conversations c
            join conversation_members cm on cm.conversation_id = c.id and cm.user_id = @user_id
            where c.kind = 'Saved'
            limit 1;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var find = new NpgsqlCommand(findExisting, connection))
        {
            find.Parameters.AddWithValue("user_id", userId);
            var existing = await find.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid existingId)
                return await GetConversationAsync(connection, existingId, userId, cancellationToken)
                       ?? throw new InvalidOperationException("Saved conversation could not be loaded.");
        }

        var conversationId = Guid.NewGuid();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await InsertConversationAsync(connection, tx, conversationId, "Saved", "Сохраненные сообщения", cancellationToken);
        await InsertMemberAsync(connection, tx, conversationId, userId, "owner", cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return await GetConversationAsync(connection, conversationId, userId, cancellationToken)
               ?? throw new InvalidOperationException("Saved conversation could not be loaded.");
    }

    public async Task<ConversationSummary> GetOrCreateAnnouncementConversationAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        const string findExisting = """
            select c.id
            from conversations c
            join conversation_members cm on cm.conversation_id = c.id and cm.user_id = @user_id
            where c.kind = 'Announcement'
            limit 1;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var find = new NpgsqlCommand(findExisting, connection))
        {
            find.Parameters.AddWithValue("user_id", userId);
            var existing = await find.ExecuteScalarAsync(cancellationToken);
            if (existing is Guid existingId)
                return await GetConversationAsync(connection, existingId, userId, cancellationToken)
                       ?? throw new InvalidOperationException("Announcement conversation could not be loaded.");
        }

        var conversationId = Guid.NewGuid();
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        await InsertConversationAsync(connection, tx, conversationId, "Announcement", "NewSunshine", cancellationToken);
        await InsertMemberAsync(connection, tx, conversationId, userId, "member", cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await SaveSystemMessageAsync(
            conversationId,
            "Привет! Ты был принят в самый защищенный и криптографический мессенджер NewSunshine. Здесь будут важные системные уведомления, обновления и сообщения администраторов.",
            cancellationToken);

        return await GetConversationAsync(connection, conversationId, userId, cancellationToken)
               ?? throw new InvalidOperationException("Announcement conversation could not be loaded.");
    }

    public async Task BroadcastAnnouncementAsync(
        string plainText,
        CancellationToken cancellationToken = default)
    {
        const string usersSql = "select id from users order by created_at;";

        var userIds = new List<Guid>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using (var users = new NpgsqlCommand(usersSql, connection))
        await using (var reader = await users.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                userIds.Add(reader.GetGuid(0));
        }

        foreach (var userId in userIds)
        {
            var conversation = await GetOrCreateAnnouncementConversationAsync(userId, cancellationToken);
            await SaveSystemMessageAsync(conversation.Id, plainText, cancellationToken);
        }
    }

    private Task<MessageDto> SaveSystemMessageAsync(
        Guid conversationId,
        string plainText,
        CancellationToken cancellationToken)
    {
        var envelope = new CryptoEnvelope(
            "system/plaintext",
            "system",
            "",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plainText)),
            "",
            "",
            "");

        return SaveMessageCoreAsync(
            conversationId,
            null,
            null,
            Guid.NewGuid(),
            plainText,
            envelope,
            [],
            null,
            cancellationToken);
    }

    public async Task<ConversationSummary> CreateGroupConversationAsync(
        Guid userId,
        CreateGroupConversationRequest request,
        CancellationToken cancellationToken = default)
    {
        var conversationId = Guid.NewGuid();
        var handles = request.MemberHandles
            .Select(NormalizeHandle)
            .Where(handle => !string.IsNullOrWhiteSpace(handle))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        await InsertConversationAsync(connection, tx, conversationId, "Group", request.Title.Trim(), cancellationToken);
        await InsertMemberAsync(connection, tx, conversationId, userId, "owner", cancellationToken);

        foreach (var handle in handles)
        {
            var peer = await FindUserByHandleAsync(handle, cancellationToken);
            if (peer is not null && peer.Id != userId)
                await InsertMemberAsync(connection, tx, conversationId, peer.Id, "member", cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return await GetConversationAsync(connection, conversationId, userId, cancellationToken)
               ?? throw new InvalidOperationException("Created group could not be loaded.");
    }

    public async Task MarkReadAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            update conversation_members
            set last_read_at = now()
            where conversation_id = @conversation_id and user_id = @user_id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteConversationAsync(
        Guid conversationId,
        Guid userId,
        DeleteConversationScope scope,
        CancellationToken cancellationToken = default)
    {
        if (!await IsConversationMemberAsync(conversationId, userId, cancellationToken))
            throw new UnauthorizedAccessException("User is not a member of the conversation.");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        if (scope == DeleteConversationScope.ForEveryone)
        {
            await using var command = new NpgsqlCommand("delete from conversations where id = @conversation_id;", connection);
            command.Parameters.AddWithValue("conversation_id", conversationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        const string sql = """
            insert into conversation_deletions (conversation_id, user_id)
            values (@conversation_id, @user_id)
            on conflict do nothing;
            """;

        await using var localCommand = new NpgsqlCommand(sql, connection);
        localCommand.Parameters.AddWithValue("conversation_id", conversationId);
        localCommand.Parameters.AddWithValue("user_id", userId);
        await localCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertConversationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid conversationId,
        string kind,
        string? title,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into conversations (id, kind, title, updated_at)
            values (@id, @kind, @title, now());
            """;

        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("id", conversationId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("title", DbValue(title));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMemberAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        Guid conversationId,
        Guid userId,
        string role,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into conversation_members (conversation_id, user_id, role)
            values (@conversation_id, @user_id, @role)
            on conflict do nothing;
            """;

        await using var command = new NpgsqlCommand(sql, connection, tx);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("role", role);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
