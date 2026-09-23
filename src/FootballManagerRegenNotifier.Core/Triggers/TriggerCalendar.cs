using System.Collections.Immutable;
using FootballManagerRegenNotifier.Core.Model;

namespace FootballManagerRegenNotifier.Core.Triggers;

/// <summary>
/// Turns year-agnostic <see cref="CountryRule"/> rows into concrete dated
/// triggers.
/// </summary>
/// <remarks>
/// Deliberately stateless and cache-free. An earlier design memoised
/// materialised years in a <see cref="Dictionary{TKey,TValue}"/>, which is read
/// by the sampler thread and written by the preview thread — concurrent writes
/// corrupt the bucket chain and hang or throw non-deterministically hours into a
/// session, which is the worst possible failure mode for a background monitor.
/// Materialising ~91 rows across a handful of years costs microseconds and
/// happens at most once per commit, so there is nothing to gain by caching it.
/// </remarks>
public sealed class TriggerCalendar
{
    private readonly ImmutableArray<CountryRule> _rules;
    private readonly int _leadDays;

    public TriggerCalendar(IEnumerable<CountryRule> rules, int leadDays = 0)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentOutOfRangeException.ThrowIfNegative(leadDays);
        _rules = [.. rules];
        _leadDays = leadDays;
    }

    public int LeadDays => _leadDays;

    /// <summary>
    /// Every trigger whose fire date falls in <c>(exclusiveFrom, inclusiveTo]</c>,
    /// ordered by date. This half-open shape is the whole point: it is what makes
    /// a jump from 1 January to 30 June report every window it flew past, instead
    /// of only firing when the clock lands exactly on the day.
    /// </summary>
    public IReadOnlyList<MaterialisedTrigger> Crossings(DateOnly exclusiveFrom, DateOnly inclusiveTo)
    {
        if (inclusiveTo <= exclusiveFrom) return [];

        var hits = new List<MaterialisedTrigger>();
        foreach (var t in Materialise(YearsSpanning(exclusiveFrom, inclusiveTo)))
        {
            if (t.FireOn > exclusiveFrom && t.FireOn <= inclusiveTo) hits.Add(t);
        }

        hits.Sort(static (a, b) =>
        {
            int c = a.FireOn.CompareTo(b.FireOn);
            return c != 0 ? c : string.CompareOrdinal(a.Key.CountryCode, b.Key.CountryCode);
        });
        return hits;
    }

    /// <summary>The soonest trigger strictly after <paramref name="after"/>. Drives the tray countdown.</summary>
    public MaterialisedTrigger? Next(DateOnly after)
    {
        MaterialisedTrigger? best = null;
        foreach (var t in Materialise([after.Year, after.Year + 1]))
        {
            if (t.FireOn <= after) continue;
            if (best is null || t.FireOn < best.FireOn) best = t;
        }
        return best;
    }

    /// <summary>
    /// Materialises every rule for each supplied occurrence year.
    /// </summary>
    public IEnumerable<MaterialisedTrigger> Materialise(IEnumerable<int> occurrenceYears)
    {
        foreach (int year in occurrenceYears)
        {
            foreach (var rule in _rules)
            {
                var open = SafeDate(year, rule.WindowStartMonth, rule.WindowStartDay);

                // A window whose end falls before its start rolls into the next
                // calendar year (Sweden's lower leagues open 25 Dec and close in
                // January). The occurrence year is always the year it OPENS, so
                // the key stays stable regardless of which side of 1 January the
                // user happens to be standing on when it fires.
                int closeYear = rule.CrossesYearBoundary ? year + 1 : year;
                var close = SafeDate(closeYear, rule.WindowEndMonth, rule.WindowEndDay);

                yield return new MaterialisedTrigger
                {
                    Key = new TriggerKey(rule.Code, year, TriggerKind.WindowOpen),
                    FireOn = open,
                    WindowOpen = open,
                    WindowClose = close,
                    Rule = rule,
                };

                if (_leadDays > 0)
                {
                    yield return new MaterialisedTrigger
                    {
                        Key = new TriggerKey(rule.Code, year, TriggerKind.Lead),
                        FireOn = open.AddDays(-_leadDays),
                        WindowOpen = open,
                        WindowClose = close,
                        Rule = rule,
                    };
                }
            }
        }
    }

    /// <summary>
    /// Occurrence years that could possibly produce a trigger inside the range.
    /// Padded by one year on each side so that a lead offset reaching back across
    /// 1 January, and a window closing after it, are both covered.
    /// </summary>
    private static int[] YearsSpanning(DateOnly from, DateOnly to)
    {
        int first = Math.Min(from.Year, to.Year) - 1;
        int last = Math.Max(from.Year, to.Year) + 1;
        var years = new int[last - first + 1];
        for (int i = 0; i < years.Length; i++) years[i] = first + i;
        return years;
    }

    /// <summary>
    /// Builds a date, clamping the day to the length of the month.
    /// </summary>
    /// <remarks>
    /// No row in the shipped dataset uses 29 February as an endpoint, so this
    /// never fires today. It exists because the dataset is a user-editable
    /// spreadsheet: somebody will eventually type 31 in a 30-day month, and the
    /// correct response to that is a clamped date and a working app, not an
    /// <see cref="ArgumentOutOfRangeException"/> on a background thread.
    /// </remarks>
    private static DateOnly SafeDate(int year, int month, int day)
    {
        int m = Math.Clamp(month, 1, 12);
        int d = Math.Clamp(day, 1, DateTime.DaysInMonth(year, m));
        return new DateOnly(year, m, d);
    }
}
