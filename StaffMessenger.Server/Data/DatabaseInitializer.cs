using Npgsql;

namespace StaffMessenger.Server.Data;

public sealed class DatabaseInitializer
{
    private readonly NpgsqlDataSource _dataSource;

    public DatabaseInitializer(NpgsqlDataSource dataSource)
        => _dataSource = dataSource;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            create table if not exists users (
                id uuid primary key,
                handle text not null,
                display_name text not null,
                password_hash text not null,
                avatar_url text null,
                about text null,
                birth_date date null,
                phone text null,
                two_factor_enabled boolean not null default false,
                totp_secret text null,
                totp_phone text null,
                allow_profile_search boolean not null default true,
                allow_phone_discovery boolean not null default false,
                show_birthday boolean not null default true,
                show_last_seen boolean not null default true,
                send_read_receipts boolean not null default true,
                encrypt_media_metadata boolean not null default true,
                show_message_notifications boolean not null default true,
                play_incoming_sound boolean not null default true,
                show_text_preview boolean not null default false,
                status text not null default 'offline',
                created_at timestamptz not null default now()
            );

            alter table users add column if not exists about text null;
            alter table users add column if not exists birth_date date null;
            alter table users add column if not exists phone text null;
            alter table users add column if not exists two_factor_enabled boolean not null default false;
            alter table users add column if not exists totp_secret text null;
            alter table users add column if not exists totp_phone text null;
            alter table users add column if not exists allow_profile_search boolean not null default true;
            alter table users add column if not exists allow_phone_discovery boolean not null default false;
            alter table users add column if not exists show_birthday boolean not null default true;
            alter table users add column if not exists show_last_seen boolean not null default true;
            alter table users add column if not exists send_read_receipts boolean not null default true;
            alter table users add column if not exists encrypt_media_metadata boolean not null default true;
            alter table users add column if not exists show_message_notifications boolean not null default true;
            alter table users add column if not exists play_incoming_sound boolean not null default true;
            alter table users add column if not exists show_text_preview boolean not null default false;

            create unique index if not exists ux_users_handle_lower on users (lower(handle));

            create table if not exists auth_identities (
                id uuid primary key,
                user_id uuid not null references users(id) on delete cascade,
                provider text not null,
                identifier text not null,
                password_hash text null,
                verified_at timestamptz null,
                created_at timestamptz not null default now()
            );

            create unique index if not exists ux_auth_identities_provider_identifier_lower
                on auth_identities (provider, lower(identifier));

            create index if not exists ix_auth_identities_user_id on auth_identities (user_id);

            create table if not exists identity_verification_codes (
                id uuid primary key,
                user_id uuid null references users(id) on delete cascade,
                provider text not null,
                identifier text not null,
                code_hash text not null,
                expires_at timestamptz not null,
                consumed_at timestamptz null,
                created_at timestamptz not null default now()
            );

            create index if not exists ix_identity_verification_codes_lookup
                on identity_verification_codes (provider, lower(identifier), expires_at desc);

            create table if not exists device_keys (
                id uuid primary key,
                user_id uuid not null references users(id) on delete cascade,
                algorithm text not null,
                key_id text not null,
                public_key_base64 text not null,
                created_at timestamptz not null default now(),
                last_seen_at timestamptz null
            );

            create unique index if not exists ux_device_keys_user_key on device_keys (user_id, key_id);

            create table if not exists sessions (
                id uuid primary key,
                user_id uuid not null references users(id) on delete cascade,
                token_hash text not null unique,
                device_key_id text null,
                device_label text null,
                user_agent text null,
                expires_at timestamptz not null,
                created_at timestamptz not null default now(),
                last_seen_at timestamptz not null default now(),
                revoked_at timestamptz null
            );

            alter table sessions add column if not exists device_key_id text null;
            alter table sessions add column if not exists device_label text null;
            alter table sessions add column if not exists user_agent text null;
            alter table sessions add column if not exists last_seen_at timestamptz not null default now();

            create table if not exists yandex_device_challenges (
                id uuid primary key,
                user_id uuid null references users(id) on delete cascade,
                device_code text not null,
                user_code text not null,
                verification_url text not null,
                expires_at timestamptz not null,
                consumed_at timestamptz null,
                created_at timestamptz not null default now()
            );

            create index if not exists ix_yandex_device_challenges_lookup
                on yandex_device_challenges (id, expires_at);

            create table if not exists conversations (
                id uuid primary key,
                kind text not null,
                title text null,
                avatar_url text null,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );

            create table if not exists conversation_members (
                conversation_id uuid not null references conversations(id) on delete cascade,
                user_id uuid not null references users(id) on delete cascade,
                role text not null default 'member',
                joined_at timestamptz not null default now(),
                last_read_at timestamptz null,
                primary key (conversation_id, user_id)
            );

            create table if not exists attachments (
                id uuid primary key,
                owner_user_id uuid not null references users(id) on delete cascade,
                kind text not null,
                file_name text not null,
                content_type text not null,
                size_bytes bigint not null,
                sha256_hex text not null,
                storage_path text not null,
                width integer null,
                height integer null,
                duration_ms integer null,
                created_at timestamptz not null default now()
            );

            create table if not exists bots (
                id uuid primary key,
                owner_user_id uuid not null references users(id) on delete cascade,
                name text not null,
                description text not null,
                webhook_url text null,
                permissions integer not null,
                signing_secret_hash text not null,
                created_at timestamptz not null default now()
            );

            create table if not exists bot_tokens (
                id uuid primary key,
                bot_id uuid not null references bots(id) on delete cascade,
                token_hash text not null unique,
                expires_at timestamptz not null,
                created_at timestamptz not null default now(),
                revoked_at timestamptz null
            );

            create table if not exists bot_conversation_members (
                bot_id uuid not null references bots(id) on delete cascade,
                conversation_id uuid not null references conversations(id) on delete cascade,
                joined_at timestamptz not null default now(),
                primary key (bot_id, conversation_id)
            );

            create table if not exists messages (
                id uuid primary key,
                conversation_id uuid not null references conversations(id) on delete cascade,
                sender_user_id uuid null references users(id) on delete set null,
                sender_bot_id uuid null references bots(id) on delete set null,
                client_message_id uuid not null,
                plain_preview text not null,
                encrypted_body jsonb not null,
                state text not null,
                reply_to_message_id uuid null references messages(id) on delete set null,
                deleted_for_everyone_at timestamptz null,
                sent_at timestamptz not null default now()
            );

            alter table messages add column if not exists deleted_for_everyone_at timestamptz null;

            create index if not exists ix_messages_conversation_sent_at on messages (conversation_id, sent_at desc);

            create table if not exists message_attachments (
                message_id uuid not null references messages(id) on delete cascade,
                attachment_id uuid not null references attachments(id) on delete cascade,
                primary key (message_id, attachment_id)
            );

            create table if not exists message_deletions (
                message_id uuid not null references messages(id) on delete cascade,
                user_id uuid not null references users(id) on delete cascade,
                deleted_at timestamptz not null default now(),
                primary key (message_id, user_id)
            );

            create table if not exists conversation_deletions (
                conversation_id uuid not null references conversations(id) on delete cascade,
                user_id uuid not null references users(id) on delete cascade,
                deleted_at timestamptz not null default now(),
                primary key (conversation_id, user_id)
            );
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
