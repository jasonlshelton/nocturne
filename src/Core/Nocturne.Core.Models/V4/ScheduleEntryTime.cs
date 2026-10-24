namespace Nocturne.Core.Models.V4;

/// <summary>When in the day a <see cref="ScheduleEntry"/> or <see cref="TargetRangeEntry"/> starts.</summary>
public static class ScheduleEntryTime
{
    /// <summary>Seconds from midnight: <paramref name="timeAsSeconds"/>, else <paramref name="time"/> parsed, else 0.</summary>
    public static int SecondsOf(int? timeAsSeconds, string time) =>
        timeAsSeconds ?? (TimeOnly.TryParse(time, out var parsed) ? (int)parsed.ToTimeSpan().TotalSeconds : 0);
}
