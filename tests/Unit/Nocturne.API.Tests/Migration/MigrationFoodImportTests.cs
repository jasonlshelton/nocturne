using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Nocturne.API.Services.Migration;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities;
using Xunit;

namespace Nocturne.API.Tests.Migration;

/// <summary>
/// A Nightscout migration re-run meets the foods an earlier run imported; what it may do to them
/// follows the connector import's rule for deleted rows.
/// </summary>
[Trait("Category", "Unit")]
public class MigrationFoodImportTests
{
    private const string OriginalId = "65f0000000000000000000f1";

    private readonly DbContextOptions<NocturneDbContext> _options = new DbContextOptionsBuilder<NocturneDbContext>()
        .UseInMemoryDatabase($"migration_food_import_{Guid.NewGuid()}")
        .Options;

    private readonly Guid _tenantId = Guid.NewGuid();

    private NocturneDbContext NewContext() => new(_options) { TenantId = _tenantId };

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task FoodImportBlocked_BlocksALiveOrUserDeletedFood_NotASystemSweptOne(bool? deletedByUser, bool blocked)
    {
        await using (var context = NewContext())
        {
            var food = new FoodEntity
            {
                Id = Guid.CreateVersion7(), OriginalId = OriginalId, Type = "food", Name = "Oats", Carbs = 30,
            };
            context.Foods.Add(food);
            if (deletedByUser is { } byUser)
            {
                food.DeletedAt = DateTime.UtcNow;
                context.Entry(food).Property("DeletedByUser").CurrentValue = byUser;
            }

            await context.SaveChangesAsync();
        }

        await using var readContext = NewContext();
        (await MigrationJob.FoodImportBlockedAsync(readContext, OriginalId, "Oats", "food", CancellationToken.None))
            .Should().Be(blocked);
        (await MigrationJob.FoodImportBlockedAsync(readContext, null, "Oats", "food", CancellationToken.None))
            .Should().Be(blocked, "a food without an id is matched by name and type");
    }
}
