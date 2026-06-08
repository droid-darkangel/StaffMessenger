# StaffMessenger

StaffMessenger is a .NET 10 + Avalonia secure messenger scaffold with a PostgreSQL backend, SignalR realtime transport, bot API, attachment storage, and a crypto layer built around device keys and envelope encryption.

## Projects

- `StaffMessenger` - cross-platform Avalonia UI shared by desktop/mobile/browser hosts.
- `StaffMessenger.Desktop` - Windows/macOS desktop launcher.
- `StaffMessenger.Server` - ASP.NET Core API, PostgreSQL schema initializer, SignalR hub, file uploads.
- `StaffMessenger.Contracts` - DTOs shared by client, server, and bot SDK.
- `StaffMessenger.Crypto` - entropy generator, ECDH device keys, AES-GCM envelopes, PBKDF2 password hashing.
- `StaffMessenger.BotSdk` - C# SDK for bots.

## Run PostgreSQL

```bash
docker compose up -d postgres
```

The server creates tables automatically on startup. Default connection string:

```text
Host=localhost;Port=5432;Database=staff_messenger;Username=postgres;Password=postgres
```

Override it with `ConnectionStrings:Postgres` or `STAFFMESSENGER_POSTGRES`.

Remote PostgreSQL example:

```bash
export STAFFMESSENGER_POSTGRES='Host=remote.company.net;Port=5432;Database=staff_messenger;Username=messenger;Password=secret;SSL Mode=Require;Trust Server Certificate=false;Pooling=true;Maximum Pool Size=100'
dotnet run --project StaffMessenger.Server --environment Production
```

## Run API

```bash
dotnet run --project StaffMessenger.Server
```

Health check:

```bash
curl http://localhost:5072/health
```

## Run Desktop App

```bash
dotnet run --project StaffMessenger.Desktop
```

The desktop client connects to `http://72.56.235.188:5072`.

## Build Release Artifacts

On an Apple Silicon Mac with .NET 10 SDK installed:

```bash
./scripts/build-release.sh
```

The command creates:

- `artifacts/release/StaffMessenger-Server-astralinux-x64.tar.gz` -
  self-contained x86_64 server and systemd installer for Astra Linux.
- `artifacts/release/StaffMessenger-macOS-arm64.dmg` - Apple Silicon macOS app.
- `artifacts/release/StaffMessenger-Windows-x64.exe` - one self-contained x64
  Windows executable.

To deploy the server, extract its archive on the Astra Linux host, run
`sudo ./install.sh`, edit `/etc/staffmessenger/appsettings.Production.json`,
and start the service with `sudo systemctl start staffmessenger`.

## Install Astra Linux Server With Curl

After publishing a GitHub Release, install the latest server package with:

```bash
curl -fsSL https://raw.githubusercontent.com/droid-darkangel/StaffMessenger/main/scripts/install-astralinux.sh \
  | sudo bash
```

To install a specific release, pass `STAFFMESSENGER_VERSION=v0.1.0`. This
direct `curl` command works without a token because the repository is public.

For temporary installation before creating a GitHub repository, serve the
project directory:

```bash
python3 -m http.server 8080
```

Then pass the archive URL:

```bash
curl -fsSL http://BUILD_HOST:8080/scripts/install-astralinux.sh \
  | sudo env STAFFMESSENGER_DOWNLOAD_URL=http://BUILD_HOST:8080/artifacts/release/StaffMessenger-Server-astralinux-x64.tar.gz bash
```

## GitHub CI/CD

The repository contains:

- `.github/workflows/ci.yml` - builds the server and desktop client on pushes
  and pull requests.
- `.github/workflows/release.yml` - builds all three release files and creates
  a GitHub Release when a `v*` tag is pushed.

Create a private GitHub repository after installing and authenticating GitHub
CLI:

```bash
gh auth login
./scripts/create-github-repo.sh StaffMessenger private
git tag v0.1.0
git push origin v0.1.0
```

Use `public` instead of `private` when the unauthenticated `curl` installer
must be available to everyone.

## Timeweb Cloud App

For Timeweb Cloud App Platform, use these application settings:

- Build command: `dotnet publish --configuration Release --output ./publish`
- Dependencies: leave empty
- Run command: `dotnet ./StaffMessenger.Server/publish/StaffMessenger.Server.dll`
- Project directory: `/StaffMessenger.Server`
- Health check path: `/health`

Required variables:

```text
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__Postgres=Host=HOST;Port=5432;Database=DATABASE;Username=USER;Password=PASSWORD;SSL Mode=Require;Trust Server Certificate=true
Storage__UploadsPath=/tmp/staffmessenger/uploads
Verification__ExposeDevelopmentCodes=false
```

The server reads Timeweb's `PORT` variable automatically and starts listening
before PostgreSQL schema initialization finishes, so the platform health check
can pass while database connection retries continue in the background.

## Crypto Boundary

The entropy generator is quantum-inspired, not a software substitute for physical QRNG hardware. It keeps the operating-system CSPRNG as the root source, dynamically reseeds with timing jitter, and can mix an optional external entropy endpoint via `STAFFMESSENGER_ENTROPY_ENDPOINT`.

Messages use ECDH P-256 device keys with AES-GCM envelope encryption in the client crypto layer. Bot messages currently use a marked `BOT-PLAINTEXT-BRIDGE` envelope because real E2EE bot participation requires explicit key-sharing policy per conversation.

## Bot API

Bot API is intentionally not shown in the desktop UI. Developers create bots through `/api/bots`, then use `StaffMessenger.BotSdk`:

```csharp
await using var bot = new StaffMessengerBotClient(new Uri("https://messenger.company.net"), token);
await bot.JoinApiConversationAsync(conversationId);
await bot.SendTextAsync(conversationId, "Deployment checklist is green.");
```

Available developer endpoints include capabilities, conversation membership, message history, realtime SignalR events, and bot message sending.
