using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Tracking;

namespace FootballManagerRegenNotifier.Core.Tests;

internal static class TestData
{
    public static CountryRule Rule(
        string code,
        int startMonth,
        int startDay,
        int endMonth,
        int endDay,
        int rating = 100,
        string? country = null) => new()
        {
            Code = code,
            Country = country ?? code,
            Continent = "Europe",
            YouthRating = rating,
            WindowStartMonth = startMonth,
            WindowStartDay = startDay,
            WindowEndMonth = endMonth,
            WindowEndDay = endDay,
            Confidence = DataConfidence.High,
            EnabledByDefault = true,
        };

    /// <summary>England: 14 March to 31 March.</summary>
    public static CountryRule England => Rule("ENG", 3, 14, 3, 31, 135, "England");

    /// <summary>Brazil: 22 September to 20 October.</summary>
    public static CountryRule Brazil => Rule("BRA", 9, 22, 10, 20, 163, "Brazil");

    /// <summary>Sweden lower leagues: opens 25 December, closes 5 January the following year.</summary>
    public static CountryRule SwedenLowerLeagues => Rule("SWE-LL", 12, 25, 1, 5, 88, "Sweden (Lower Leagues)");

    /// <summary>Mexico: 28 February to 2 March, straddling 29 February in a leap year.</summary>
    public static CountryRule Mexico => Rule("MEX", 2, 28, 3, 2, 120, "Mexico");

    /// <summary>A clean, high-confidence reading.</summary>
    public static Observation Good(int year, int month, int day) =>
        Observation.Read(new DateOnly(year, month, day), $"{day:00}/{month:00}/{year}", 0.95, 0.92);

    public static Observation Good(DateOnly d) => Good(d.Year, d.Month, d.Day);

    /// <summary>Feeds the same observation until the tracker commits it.</summary>
    public static TrackerState Commit(DateTracker tracker, TrackerState state, DateOnly date,
        Func<CountryRule, bool>? armed = null, List<TrackerEvent>? sink = null)
    {
        armed ??= static _ => true;
        for (int i = 0; i < tracker.Config.ConfirmSamples; i++)
        {
            var result = tracker.Step(state, Good(date), armed);
            state = result.State;
            sink?.AddRange(result.Events);
        }
        return state;
    }

    public static IReadOnlyList<TrackerEvent> Alerts(this IEnumerable<TrackerEvent> events) =>
        [.. events.Where(e => e.IsAlert)];
}
