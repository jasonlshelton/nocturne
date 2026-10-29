using FluentAssertions;
using Nocturne.API.Services.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.V4;

public class LegacyTreatmentTablesTests
{
    [Fact]
    public void ByServedPrecedence_HoldsEveryKeyedTableOnce()
    {
        var keyed = LegacyTreatmentTables.All.OfType<ILegacyKeyedTreatmentTable>().ToList();

        LegacyTreatmentTables.ByServedPrecedence.Should().HaveCount(keyed.Count);
        LegacyTreatmentTables.ByServedPrecedence.ToHashSet().SetEquals(keyed).Should().BeTrue();
    }

    [Fact]
    public void All_OnlyStateSpanTablesAreUnkeyed()
    {
        LegacyTreatmentTables.All.Where(table => table is not ILegacyKeyedTreatmentTable).ToHashSet()
            .SetEquals(LegacyTreatmentTables.StateSpanTables).Should().BeTrue();
    }

    [Theory]
    [InlineData(StateSpanCategory.Override, "t1", true)]
    [InlineData(StateSpanCategory.TemporaryTarget, "t1", true)]
    [InlineData(StateSpanCategory.Override, null, false)]
    [InlineData(StateSpanCategory.PumpMode, "t1", false)]
    public void ServesStateSpan_IsTheServedCategoriesOfSpansWrittenFromATreatment(
        StateSpanCategory category, string? originalId, bool served)
    {
        LegacyTreatmentTables.ServesStateSpan(new StateSpan { Category = category, OriginalId = originalId })
            .Should().Be(served);
    }
}
