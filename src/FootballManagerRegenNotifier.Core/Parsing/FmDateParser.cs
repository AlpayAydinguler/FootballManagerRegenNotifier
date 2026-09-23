using System.Globalization;
using System.Text;

namespace FootballManagerRegenNotifier.Core.Parsing;

/// <summary>Which component comes first in the on-screen date.</summary>
public enum DateOrder
{
    /// <summary>dd/MM/yyyy - Football Manager's default in most regions.</summary>
    DayFirst = 0,

    /// <summary>MM/dd/yyyy - the US preference setting.</summary>
    MonthFirst = 1,

    /// <summary>yyyy/MM/dd.</summary>
    YearFirst = 2,
}

public sealed record DateParseOptions
{
    /// <summary>The order the user says the game is displaying.</summary>
    public DateOrder Preferred { get; init; } = DateOrder.DayFirst;

    /// <summary>
    /// Let the parser lock the order in once it sees an unambiguous date.
    /// </summary>
    /// <remarks>
    /// 03/04/2026 is genuinely ambiguous and no amount of image tuning resolves
    /// it. But 17/03/2026 is not: a leading component above 12 can only be a day.
    /// Watching for the first such date and remembering the answer costs nothing
    /// and removes the most common silent-wrong-answer failure, where every date
    /// parses successfully and half of them mean the wrong month.
    /// </remarks>
    public bool AutoLearnOrder { get; init; } = true;

    /// <summary>Order learned from an unambiguous observation, if any.</summary>
    public DateOrder? LearnedOrder { get; init; }

    /// <summary>
    /// The order actually used. An explicitly chosen order always wins: a user
    /// who turns auto-learn off to force day-first must not be overridden by a
    /// stale learned value from a previous save.
    /// </summary>
    public DateOrder Effective => AutoLearnOrder ? LearnedOrder ?? Preferred : Preferred;
}

public sealed record DateParseResult(
    DateOnly? Date,
    string Normalised,
    DateOrder? LearnedOrder,
    string? Failure)
{
    public bool Success => Date is not null;
}

/// <summary>
/// Turns raw OCR text into a date.
/// </summary>
/// <remarks>
/// Always invariant-culture and always <c>TryParseExact</c> against an explicit
/// format list. Parsing with the current culture would make the app's behaviour
/// depend on the Windows regional settings of whoever is running it, which is a
/// bug that only ever reproduces on someone else's machine.
/// </remarks>
public static class FmDateParser
{
    private static readonly string[] DayFirstFormats =
        ["dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy"];

    private static readonly string[] MonthFirstFormats =
        ["MM/dd/yyyy", "M/d/yyyy", "MM/dd/yy", "M/d/yy"];

    private static readonly string[] YearFirstFormats =
        ["yyyy/MM/dd", "yyyy/M/d"];

    public static DateParseResult Parse(string? raw, DateParseOptions? options = null)
    {
        options ??= new DateParseOptions();

        string normalised = Normalise(raw);
        if (normalised.Length == 0)
        {
            return new DateParseResult(null, normalised, null, "No digits found.");
        }

        var parts = normalised.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return new DateParseResult(null, normalised, null,
                $"Expected three date components, got {parts.Length}.");
        }

        DateOrder? learned = options.AutoLearnOrder ? Learn(parts, options) : null;
        DateOrder order = learned ?? options.Effective;

        string[] formats = order switch
        {
            DateOrder.MonthFirst => MonthFirstFormats,
            DateOrder.YearFirst => YearFirstFormats,
            _ => DayFirstFormats,
        };

        if (DateTime.TryParseExact(normalised, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return new DateParseResult(DateOnly.FromDateTime(parsed), normalised, learned, null);
        }

        return new DateParseResult(null, normalised, learned,
            $"\"{normalised}\" is not a valid {order} date.");
    }

    /// <summary>
    /// Detects the component order from a date that can only be read one way.
    /// Returns null when the sample is ambiguous, leaving the current setting alone.
    /// </summary>
    private static DateOrder? Learn(string[] parts, DateParseOptions options)
    {
        if (parts[0].Length == 4) return DateOrder.YearFirst;

        if (!int.TryParse(parts[0], out int first) || !int.TryParse(parts[1], out int second))
        {
            return options.LearnedOrder;
        }

        if (first > 12 && second <= 12) return DateOrder.DayFirst;
        if (second > 12 && first <= 12) return DateOrder.MonthFirst;

        // Ambiguous (both <= 12). Keep whatever we already knew.
        return options.LearnedOrder;
    }

    /// <summary>
    /// Reduces raw OCR output to bare digits and separators.
    /// </summary>
    /// <remarks>
    /// Tesseract routinely emits stray spaces, and the various dash and dot
    /// characters a UI font can produce all mean "separator" here. Collapsing
    /// them before parsing is cheaper and far more predictable than trying to
    /// enumerate every format the engine might hand back.
    /// </remarks>
    public static string Normalise(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var sb = new StringBuilder(raw.Length);
        bool lastWasSeparator = false;

        foreach (char c in raw)
        {
            if (char.IsAsciiDigit(c))
            {
                sb.Append(c);
                lastWasSeparator = false;
            }
            else if (c is '/' or '\\' or '-' or '.' or '|' or ':' or ',')
            {
                // Collapse runs, and never lead with a separator.
                if (sb.Length > 0 && !lastWasSeparator)
                {
                    sb.Append('/');
                    lastWasSeparator = true;
                }
            }
            // Everything else (spaces, letters, noise) is dropped.
        }

        // Trim a trailing separator.
        while (sb.Length > 0 && sb[^1] == '/') sb.Length--;

        return sb.ToString();
    }
}
