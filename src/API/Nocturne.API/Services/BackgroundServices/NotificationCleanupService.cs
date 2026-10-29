using Nocturne.Infrastructure.Data.Abstractions;

namespace Nocturne.API.Services.BackgroundServices;

/// <summary>
/// Background service that purges archived in-app notifications older than 30 days.
/// Runs once every 24 hours to prevent unbounded growth of the notifications table.
/// </summary>
/// <remarks>
/// Only archived notifications (those already dismissed or actioned by the user) are eligible
/// for deletion. Active or unread notifications are never touched.
/// </remarks>
public class NotificationCleanupService(
    IServiceProvider serviceProvider,
    ILogger<NotificationCleanupService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var repository = scope.ServiceProvider
                    .GetRequiredService<IInAppNotificationRepository>();

                var cutoff = timeProvider.GetUtcNow().UtcDateTime - RetentionPeriod;
                var deleted = await repository.DeleteArchivedBeforeAsync(cutoff, stoppingToken);

                if (deleted > 0)
                {
                    logger.LogInformation(
                        "Cleaned up {Count} archived notifications older than {Cutoff}",
                        deleted, cutoff);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error during notification cleanup");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
