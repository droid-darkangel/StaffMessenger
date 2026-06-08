using StaffMessenger.Server.Data;

namespace StaffMessenger.Server.Services;

public sealed class DatabaseInitializationService(
    DatabaseInitializer initializer,
    ILogger<DatabaseInitializationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;

            try
            {
                await initializer.InitializeAsync(stoppingToken);
                logger.LogInformation("PostgreSQL schema is ready.");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, 2 * attempt));
                logger.LogWarning(
                    exception,
                    "PostgreSQL initialization failed on attempt {Attempt}. Retrying in {DelaySeconds}s...",
                    attempt,
                    delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }
}
