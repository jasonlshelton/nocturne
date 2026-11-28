using System.Reflection;
using System.Runtime.Serialization;

namespace Nocturne.Core.Models;

/// <summary>
/// <see cref="EnumWireNames{TEnum}"/> for a caller that holds the enum as a <see cref="Type"/>. It
/// reads a member by the same rule: its <see cref="EnumMemberAttribute"/> value, else its own name.
/// </summary>
public static class EnumWireNames
{
    /// <summary>The wire name of each member of <paramref name="enumType"/>, in declaration order.</summary>
    public static IEnumerable<string> Names(Type enumType) => Members(enumType).Select(Of);

    /// <summary>The member of <paramref name="enumType"/> whose wire name is <paramref name="name"/>, or null for none.</summary>
    public static object? Parse(Type enumType, string? name) =>
        name is null ? null : Members(enumType).FirstOrDefault(m => Of(m) == name)?.GetValue(null);

    internal static string Of(FieldInfo member) => member.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? member.Name;

    private static IEnumerable<FieldInfo> Members(Type enumType) =>
        enumType.GetFields(BindingFlags.Public | BindingFlags.Static);
}

/// <summary>
/// The <see cref="EnumMemberAttribute"/> value of each member of <typeparamref name="TEnum"/>, read
/// once, for a call site that stores, sends or reads the wire string without a serialiser. A member
/// with no attribute is its own name.
/// </summary>
public static class EnumWireNames<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<TEnum, string> Names = typeof(TEnum)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .GroupBy(f => (TEnum)f.GetValue(null)!)
        .ToDictionary(
            g => g.Key,
            g => EnumWireNames.Of(g.Last()));

    private static readonly Dictionary<string, TEnum> Values = ReverseOf(StringComparer.Ordinal);

    private static readonly Dictionary<string, TEnum> ValuesIgnoringCase = ReverseOf(StringComparer.OrdinalIgnoreCase);

    public static string Of(TEnum value) =>
        Names.TryGetValue(value, out var name) ? name : value.ToString();

    /// <summary>
    /// The member whose wire name is <paramref name="name"/>, or null for none. A name two members
    /// share reads as the first of them.
    /// </summary>
    public static TEnum? Parse(string? name, bool ignoreCase = false) =>
        name is not null && (ignoreCase ? ValuesIgnoringCase : Values).TryGetValue(name, out var value)
            ? value
            : null;

    public static string Join(IEnumerable<TEnum> values, string separator = ", ") =>
        string.Join(separator, values.Select(Of));

    private static Dictionary<string, TEnum> ReverseOf(StringComparer comparer)
    {
        var reverse = new Dictionary<string, TEnum>(comparer);
        foreach (var (value, name) in Names)
            reverse.TryAdd(name, value);
        return reverse;
    }
}
