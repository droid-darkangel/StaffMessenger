using System.Text.Json;
using Npgsql;
using StaffMessenger.Contracts.Attachments;
using StaffMessenger.Contracts.Conversations;
using StaffMessenger.Contracts.Crypto;

namespace StaffMessenger.Server.Data;

public sealed partial class MessengerRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;

    public MessengerRepository(NpgsqlDataSource dataSource)
        => _dataSource = dataSource;

    public async Task<bool> IsConversationMemberAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select exists (
                select 1 from conversation_members
                where conversation_id = @conversation_id and user_id = @user_id
            );
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversation_id", conversationId);
        command.Parameters.AddWithValue("user_id", userId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private async Task<IReadOnlyList<MemberSummary>> GetMembersAsync(
        NpgsqlConnection connection,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select u.id, u.handle, u.display_name, u.avatar_url, u.status, dk.last_seen_at
            from conversation_members cm
            join users u on u.id = cm.user_id
            left join lateral (
                select max(last_seen_at) as last_seen_at
                from device_keys
                where user_id = u.id
            ) dk on true
            where cm.conversation_id = @conversation_id
            order by cm.joined_at;
            """;

        var members = new List<MemberSummary>();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("conversation_id", conversationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            members.Add(new MemberSummary(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4).Equals("online", StringComparison.OrdinalIgnoreCase),
                reader.IsDBNull(5) ? null : ReadInstant(reader, 5)));
        }

        return members;
    }

    private static ConversationKind ReadConversationKind(string value)
        => Enum.TryParse<ConversationKind>(value, true, out var kind) ? kind : ConversationKind.Group;

    private static AttachmentKind ReadAttachmentKind(string value)
        => Enum.TryParse<AttachmentKind>(value, true, out var kind) ? kind : AttachmentKind.File;

    private static DateTimeOffset ReadInstant(NpgsqlDataReader reader, int ordinal)
    {
        var value = reader.GetDateTime(ordinal);
        return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    }

    private static object DbValue<T>(T? value)
        => value is null ? DBNull.Value : value;

    private static string NormalizeHandle(string handle)
        => handle.Trim().TrimStart('@').ToLowerInvariant();

    private static CryptoEnvelope ReadEnvelope(string json)
        => JsonSerializer.Deserialize<CryptoEnvelope>(json, JsonOptions)
               ?? throw new InvalidOperationException("Message crypto envelope is empty.");
}
