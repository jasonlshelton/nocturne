using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Nocturne.API.Services.BackgroundServices;
using Nocturne.Infrastructure.Data.Abstractions;
using Xunit;

namespace Nocturne.API.Tests.Services.BackgroundServices;

[Trait("Category", "Unit")]
public class NotificationCleanupServiceTests
{
    [Fact]
    public async Task Cleanup_purges_notifications_archived_before_the_retention_cutoff_of_the_clock()
    {
        var now = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var cutoff = new TaskCompletionSource<DateTime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Mock<IInAppNotificationRepository>();
        repository
            .Setup(r => r.DeleteArchivedBeforeAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback((DateTime before, CancellationToken _) => cutoff.TrySetResult(before))
            .ReturnsAsync(0);
        var provider = new ServiceCollection().AddSingleton(repository.Object).BuildServiceProvider();
        using var service = new NotificationCleanupService(
            provider, NullLogger<NotificationCleanupService>.Instance, new FakeTimeProvider(now));

        await service.StartAsync(CancellationToken.None);
        var purgedBefore = await cutoff.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        purgedBefore.Should().Be(now.UtcDateTime - TimeSpan.FromDays(30));
    }
}
