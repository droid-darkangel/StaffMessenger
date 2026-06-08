using System.Text.Json.Serialization;
using Npgsql;
using StaffMessenger.Contracts.App;
using StaffMessenger.Crypto.Encryption;
using StaffMessenger.Crypto.Entropy;
using StaffMessenger.Crypto.Identity;
using StaffMessenger.Server.Data;
using StaffMessenger.Server.Endpoints;
using StaffMessenger.Server.Realtime;
using StaffMessenger.Server.Security;
using StaffMessenger.Server.Services;

var builder = WebApplication.CreateBuilder(args);

ConfigureListeningUrls(builder);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
                       ?? Environment.GetEnvironmentVariable("STAFFMESSENGER_POSTGRES")
                       ?? new DatabaseOptions().ConnectionString;

builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddHostedService<DatabaseInitializationService>();
builder.Services.AddScoped<MessengerRepository>();
builder.Services.AddSingleton<IQuantumEntropyGenerator, QuantumInspiredEntropyGenerator>();
builder.Services.AddSingleton<EnvelopeEncryptionService>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<TotpService>();
builder.Services.AddSingleton<YandexOAuthService>();
builder.Services.AddSingleton<FileStorageService>();
builder.Services.AddHttpClient<IVerificationCodeSender, VerificationCodeSender>();
builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true);
    });
});

var app = builder.Build();

app.UseCors();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (NpgsqlException exception) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseAvailability");
        logger.LogError(exception, "PostgreSQL is unavailable while serving {Path}.", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await Results.Json(new
        {
            error = "Database is unavailable.",
            message = "PostgreSQL connection timed out. Try again after the server restores database connectivity."
        }).ExecuteAsync(context);
    }
    catch (TimeoutException exception) when (context.Request.Path.StartsWithSegments("/api") && !context.Response.HasStarted)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseAvailability");
        logger.LogError(exception, "Timeout while serving {Path}.", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await Results.Json(new
        {
            error = "Service timeout.",
            message = "The server timed out while processing this request."
        }).ExecuteAsync(context);
    }
});
app.UseMiddleware<ApiTokenAuthenticationMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    name = "StaffMessenger API",
    stack = ".NET 10 + PostgreSQL + SignalR",
    status = "online"
}));

app.MapGet("/health", (IQuantumEntropyGenerator entropy) => Results.Ok(new
{
    status = "healthy",
    entropy = entropy.Snapshot
}));

app.MapGet("/api/app/info", (IConfiguration configuration) =>
{
    const string currentVersion = "0.1.0-enterprise-preview";
    var latestVersion = configuration["Updates:LatestVersion"] ?? currentVersion;
    return Results.Ok(new AppUpdateInfoDto(
        "NewSunshine StaffMessenger",
        currentVersion,
        latestVersion,
        !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase),
        configuration["Updates:ReleaseNotes"] ?? "Клиент подключен к серверу обновлений NewSunshine.",
        configuration["Updates:DownloadUrl"]));
});

app.MapAuthEndpoints();
app.MapConversationEndpoints();
app.MapMessageEndpoints();
app.MapAttachmentEndpoints();
app.MapBotEndpoints();
app.MapHub<MessageHub>("/hubs/messages");

app.Run();

static void ConfigureListeningUrls(WebApplicationBuilder builder)
{
    var platformPort = Environment.GetEnvironmentVariable("PORT");
    if (int.TryParse(platformPort, out var port) && port is > 0 and <= 65535)
    {
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        return;
    }

    if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        builder.WebHost.UseUrls("http://0.0.0.0:8080");
}
