using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Nocturne.API.Reports;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Reports;

/// <summary>
/// A report enum is written into specs, catalogues and error texts through <see cref="EnumWireNames{TEnum}"/>
/// and read back from a spec by the serialiser, so the two must agree on every member.
/// </summary>
[Trait("Category", "Unit")]
public class ReportEnumWireNamesTests
{
    private static bool IsWireNamed(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static).Any(f =>
            f.GetCustomAttribute<EnumMemberAttribute>() is not null
            || f.GetCustomAttribute<JsonStringEnumMemberNameAttribute>() is not null);

    private static Type ElementOf(Type type) =>
        Nullable.GetUnderlyingType(type)
        ?? (type.IsArray ? type.GetElementType() : type.IsGenericType ? type.GetGenericArguments()[^1] : null)
        ?? type;

    private static IEnumerable<Type> ReportEnumTypes()
    {
        var assembly = typeof(ReportMark).Assembly;
        var reportEnums = assembly.GetTypes()
            .Where(t => t.IsEnum && t.Namespace?.StartsWith("Nocturne.API.Reports", StringComparison.Ordinal) == true)
            .Where(IsWireNamed);
        var parameterEnums = assembly.GetTypes()
            .Where(t => typeof(ReportDatasetParameters).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => ElementOf(ElementOf(p.PropertyType)))
            .Where(t => t.IsEnum);
        return reportEnums.Concat(parameterEnums).Append(typeof(ChartColor)).Distinct().OrderBy(t => t.Name, StringComparer.Ordinal);
    }

    public static IEnumerable<object[]> WireEnums => ReportEnumTypes().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(WireEnums))]
    public void A_wire_name_is_what_the_serialiser_writes_and_parses_back_to_its_member(Type enumType) =>
        typeof(ReportEnumWireNamesTests)
            .GetMethod(nameof(Check), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(enumType)
            .Invoke(null, null);

    private static void Check<T>() where T : struct, Enum
    {
        Enum.GetValues<T>().Should().NotBeEmpty();

        foreach (var value in Enum.GetValues<T>())
        {
            var name = EnumWireNames<T>.Of(value);

            name.Should().Be(JsonSerializer.Serialize(value).Trim('"'), "{0}.{1}", typeof(T).Name, value);
            EnumWireNames<T>.Parse(name).Should().Be(value, "{0}.{1}", typeof(T).Name, value);
        }
    }

    [Fact]
    public void A_name_that_no_member_has_parses_to_null()
    {
        EnumWireNames<ReportFieldKind>.Parse("TimeOfDay").Should().BeNull("the wire name is time_of_day");
        EnumWireNames<ReportFieldKind>.Parse("TIME_OF_DAY").Should().BeNull("the match is ordinal");
        EnumWireNames<ReportFieldKind>.Parse(null).Should().BeNull();
        EnumWireNames<ReportFieldKind>.Parse("time_of_day").Should().Be(ReportFieldKind.TimeOfDay);
    }

    [Fact]
    public void A_list_of_names_is_joined_by_the_separator()
    {
        EnumWireNames<ReportFieldKind>.Join([ReportFieldKind.Time, ReportFieldKind.TimeOfDay]).Should().Be("time, time_of_day");
        EnumWireNames<ReportWeekday>.Join([ReportWeekday.Mon, ReportWeekday.Tue], " ").Should().Be("Mon Tue");
    }
}
