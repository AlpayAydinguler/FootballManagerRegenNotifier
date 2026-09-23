using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using FootballManagerRegenNotifier.Core.Model;

namespace FootballManagerRegenNotifier.App.ViewModels;

/// <summary>One country in the dashboard.</summary>
public sealed partial class CountryRowViewModel(CountryRule rule) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public CountryRule Rule { get; } = rule ?? throw new ArgumentNullException(nameof(rule));

    public string Code => Rule.Code;

    public string Country => Rule.Country;

    public string Continent => Rule.Continent;

    public int? YouthRating => Rule.YouthRating;

    /// <summary>Sorts as a day-of-year so that March and September order sensibly.</summary>
    public int WindowSortKey => Rule.WindowStartMonth * 100 + Rule.WindowStartDay;

    public bool IsLowerLeague => Rule.IsLowerLeague;

    public DataConfidence Confidence => Rule.Confidence;

    public string Window
    {
        get
        {
            string start = Format(Rule.WindowStartDay, Rule.WindowStartMonth);
            if (Rule.WindowStartMonth == Rule.WindowEndMonth && Rule.WindowStartDay == Rule.WindowEndDay)
            {
                return start;
            }
            return $"{start} – {Format(Rule.WindowEndDay, Rule.WindowEndMonth)}";
        }
    }

    public string RatingDisplay =>
        YouthRating?.ToString(CultureInfo.InvariantCulture) ?? "–";

    public string Tooltip
    {
        get
        {
            string confidence = Confidence switch
            {
                DataConfidence.High => "Corroborated.",
                DataConfidence.Low => "Known to be stale or ambiguous — verify against your save.",
                _ => "Sourced but uncorroborated, or an inactive-nation default date.",
            };
            return Rule.Notes.Length > 0
                ? $"{confidence}\n{Rule.Notes}"
                : confidence;
        }
    }

    private static string Format(int day, int month) =>
        $"{day} {CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month)}";
}
