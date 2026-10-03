using System.Text.Json;

namespace Nocturne.Core.Models;

/// <summary>
/// Carries the <c>timestamp</c> a treatment was uploaded with between a <see cref="Treatment"/> and
/// the V4 records it decomposes into, beside <see cref="TreatmentClientId"/>, so it is served back
/// verbatim as Nightscout stores it.
/// </summary>
/// <remarks>
/// NightscoutKit drops a served treatment that has no string <c>timestamp</c>, so a Loop follower
/// (LoopCaregiver) sees none of Loop's own uploads without it. Nothing is invented for a treatment
/// uploaded without one: AAPS reads <c>timestamp</c> as a number, and a string it cannot parse
/// fails its whole page.
/// </remarks>
public static class TreatmentUploadedTimestamp
{
    public const string Field = "timestamp";

    /// <summary>
    /// <paramref name="record"/> with the uploaded timestamp of <paramref name="treatment"/> added, or
    /// unchanged when it carries none.
    /// </summary>
    public static Dictionary<string, object?>? AddTo(Dictionary<string, object?>? record, Treatment treatment)
    {
        if (string.IsNullOrEmpty(treatment.Timestamp))
            return record;

        record ??= new();
        record[Field] = treatment.Timestamp;
        return record;
    }

    /// <summary>The uploaded timestamp a record's additional properties hold, or null.</summary>
    public static string? Of(IReadOnlyDictionary<string, object?>? record) =>
        record?.GetValueOrDefault(Field) switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            _ => null,
        };
}
