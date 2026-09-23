namespace FootballManagerRegenNotifier.Core.Model;

/// <summary>
/// One row of the youth-intake dataset: a nation and the calendar window during
/// which its clubs generate newgens.
/// </summary>
/// <remarks>
/// Dates are stored as month/day integers rather than <see cref="DateOnly"/>
/// because a rule is year-agnostic — it is materialised into concrete dates for
/// a given in-game year by <c>TriggerCalendar</c>.
/// </remarks>
public sealed record CountryRule
{
    /// <summary>
    /// Stable selection key (e.g. <c>BRA</c>, <c>BRA-LL</c>). Selections persist
    /// against this, never against <see cref="Country"/>, so renaming a country
    /// in the spreadsheet does not silently clear the user's picks.
    /// </summary>
    public required string Code { get; init; }

    public required string Country { get; init; }

    public required string Continent { get; init; }

    /// <summary>Nation youth rating on FM's 0-200 scale. Null when unsourced.</summary>
    public int? YouthRating { get; init; }

    public required int WindowStartMonth { get; init; }
    public required int WindowStartDay { get; init; }
    public required int WindowEndMonth { get; init; }
    public required int WindowEndDay { get; init; }

    public DataConfidence Confidence { get; init; } = DataConfidence.Medium;

    public bool EnabledByDefault { get; init; }

    public string Notes { get; init; } = string.Empty;

    /// <summary>
    /// True when the intake window runs past 31 December into the next calendar
    /// year (e.g. Sweden's lower leagues open on 25 December).
    /// </summary>
    public bool CrossesYearBoundary =>
        (WindowEndMonth, WindowEndDay).CompareTo((WindowStartMonth, WindowStartDay)) < 0;

    /// <summary>
    /// Lower-league rows are excluded from "Select Top Tier" by default: a user
    /// clicking it expects the elite nations, not four extra derived rows.
    /// </summary>
    public bool IsLowerLeague => Code.EndsWith("-LL", StringComparison.Ordinal);
}

public enum DataConfidence
{
    Low = 0,
    Medium = 1,
    High = 2,
}
