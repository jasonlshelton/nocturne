using System.Text.Json;
using FluentAssertions;
using Nocturne.Core.Models.Alerts;
using Nocturne.Core.Models.Health;
using Xunit;

namespace Nocturne.Core.Models.Tests;

[Trait("Category", "Unit")]
public class EnumWireNamesTests
{
    [Fact]
    public void ChannelType_reads_as_the_serialiser_writes_it() => ReadsAsTheSerialiserWritesIt<ChannelType>();

    [Fact]
    public void AlertChannelState_reads_as_the_serialiser_writes_it() => ReadsAsTheSerialiserWritesIt<AlertChannelState>();

    [Fact]
    public void GoogleHealthSyncPhase_reads_as_the_serialiser_writes_it() => ReadsAsTheSerialiserWritesIt<GoogleHealthSyncPhase>();

    [Fact]
    public void ExcursionCloseReason_reads_as_the_serialiser_writes_it() => ReadsAsTheSerialiserWritesIt<ExcursionCloseReason>();

    [Theory]
    [InlineData(ExcursionCloseReason.Hysteresis, "hysteresis")]
    [InlineData(ExcursionCloseReason.AutoResolve, "auto")]
    [InlineData(ExcursionCloseReason.Manual, "manual")]
    [InlineData(ExcursionCloseReason.RuleDisabled, "rule-disabled")]
    [InlineData(ExcursionCloseReason.HoldBound, "hold-bound")]
    public void A_close_reason_is_stored_as_the_string_old_rows_carry(ExcursionCloseReason reason, string stored)
    {
        reason.ToWireString().Should().Be(stored);
    }

    [Fact]
    public void Every_close_reason_has_a_pinned_stored_string()
    {
        Enum.GetValues<ExcursionCloseReason>().Should().HaveCount(5);
    }

    [Fact]
    public void A_close_reason_outside_the_enum_reads_as_its_number()
    {
        ((ExcursionCloseReason)99).ToWireString().Should().Be("99");
    }

    [Fact]
    public void ChannelType_parses_back_each_of_its_wire_names() => ParsesBackItsWireNames<ChannelType>();

    [Fact]
    public void ExcursionCloseReason_parses_back_each_of_its_wire_names() => ParsesBackItsWireNames<ExcursionCloseReason>();

    [Fact]
    public void A_name_no_member_has_parses_to_null()
    {
        EnumWireNames<ExcursionCloseReason>.Parse("AutoResolve").Should().BeNull("its wire name is auto");
        EnumWireNames<ExcursionCloseReason>.Parse("99").Should().BeNull();
        EnumWireNames<ExcursionCloseReason>.Parse(null).Should().BeNull();
    }

    [Fact]
    public void A_name_matches_by_case_only_when_asked()
    {
        EnumWireNames<ExcursionCloseReason>.Parse("AUTO").Should().BeNull();
        EnumWireNames<ExcursionCloseReason>.Parse("AUTO", ignoreCase: true).Should().Be(ExcursionCloseReason.AutoResolve);
    }

    private enum Shared
    {
        [System.Runtime.Serialization.EnumMember(Value = "same")] First,
        [System.Runtime.Serialization.EnumMember(Value = "same")] Second,
        Third,
    }

    [Fact]
    public void A_name_two_members_share_parses_to_the_first()
    {
        EnumWireNames<Shared>.Parse("same").Should().Be(Shared.First);
        EnumWireNames<Shared>.Parse("Third").Should().Be(Shared.Third);
    }

    [Fact]
    public void A_list_of_values_is_joined_by_their_wire_names()
    {
        EnumWireNames<ExcursionCloseReason>.Join([ExcursionCloseReason.AutoResolve, ExcursionCloseReason.Manual])
            .Should().Be("auto, manual");
        EnumWireNames<ExcursionCloseReason>.Join([ExcursionCloseReason.AutoResolve, ExcursionCloseReason.Manual], "|")
            .Should().Be("auto|manual");
    }

    [Fact]
    public void A_type_held_at_run_time_reads_the_names_the_generic_reader_does()
    {
        EnumWireNames.Names(typeof(ExcursionCloseReason))
            .Should().Equal(Enum.GetValues<ExcursionCloseReason>().Select(EnumWireNames<ExcursionCloseReason>.Of));

        EnumWireNames.Parse(typeof(ExcursionCloseReason), "auto").Should().Be(ExcursionCloseReason.AutoResolve);
        EnumWireNames.Parse(typeof(ExcursionCloseReason), "AutoResolve").Should().BeNull();
        EnumWireNames.Parse(typeof(ExcursionCloseReason), null).Should().BeNull();
    }

    private static void ReadsAsTheSerialiserWritesIt<T>() where T : struct, Enum
    {
        Enum.GetValues<T>().Should().NotBeEmpty();

        foreach (var value in Enum.GetValues<T>())
            EnumWireNames<T>.Of(value).Should().Be(JsonSerializer.Serialize(value).Trim('"'), "{0}", value);
    }

    private static void ParsesBackItsWireNames<T>() where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
            EnumWireNames<T>.Parse(EnumWireNames<T>.Of(value)).Should().Be(value, "{0}", value);
    }
}
