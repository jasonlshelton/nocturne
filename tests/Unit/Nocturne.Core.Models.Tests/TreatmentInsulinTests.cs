using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.Core.Models.Tests;

/// <summary>
/// AAPS classifies any served treatment carrying <c>insulin &gt; 0</c> as a bolus before it reads
/// <c>eventType</c>, so a temp basal must never be served with an insulin amount it did not carry.
/// </summary>
[Trait("Category", "Unit")]
public class TreatmentInsulinTests
{
    [Fact]
    public void Insulin_IsNotDerivedFromRateAndDuration()
    {
        var tempBasal = new Treatment { EventType = "Temp Basal", Rate = 1.25, Absolute = 1.25, Duration = 30 };

        tempBasal.Insulin.Should().BeNull();
        JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(tempBasal))
            .TryGetProperty("insulin", out var insulin).Should().BeTrue();
        insulin.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Insulin_StillFallsBackToAmount()
    {
        new Treatment { Amount = 2.5 }.Insulin.Should().Be(2.5);
        new Treatment { Insulin = 1.5, Amount = 2.5 }.Insulin.Should().Be(1.5);
    }

    [Fact]
    public void Duration_IsStillDerivedFromInsulinAndRate()
    {
        new Treatment { Insulin = 0.5, Rate = 1.0 }.Duration.Should().Be(30);
    }
}
