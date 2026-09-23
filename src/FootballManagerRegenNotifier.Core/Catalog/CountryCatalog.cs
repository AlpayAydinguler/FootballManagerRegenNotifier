using ClosedXML.Excel;
using FootballManagerRegenNotifier.Core.Model;

namespace FootballManagerRegenNotifier.Core.Catalog;

public sealed record CatalogLoadResult(
    IReadOnlyList<CountryRule> Rules,
    IReadOnlyList<string> Warnings,
    CatalogSource Source)
{
    public bool HasWarnings => Warnings.Count > 0;
}

public enum CatalogSource
{
    Workbook,
    SeedBecauseMissing,
    SeedBecauseUnreadable,
}

/// <summary>
/// Reads and writes the user-editable youth intake spreadsheet.
/// </summary>
/// <remarks>
/// The spreadsheet is the version-agnostic seam. Adapting the tool to FM27 or
/// back to FM24 should mean editing dates in Excel and re-calibrating a
/// rectangle, never editing code, so the reader is deliberately tolerant: it
/// reports problems row by row and returns everything it did understand rather
/// than refusing the file.
/// </remarks>
public static class CountryCatalog
{
    public const string DataSheetName = "Countries";
    public const string SourcesSheetName = "Sources";

    private static readonly string[] Headers =
    [
        "Code", "Country", "Continent", "YouthRating",
        "StartMonth", "StartDay", "EndMonth", "EndDay",
        "Confidence", "EnabledByDefault", "Notes",
    ];

    public const string Attribution =
        "Youth intake windows compiled from Passion4FM, \"Football Manager Youth Intake Dates\" " +
        "(https://www.passion4fm.com/football-manager-youth-intake-dates/), independently re-derived " +
        "and corrected. Nation youth ratings (0-200 scale) from FM Scout " +
        "(https://www.fmscout.com/a-fm22-youth-ratings-all-nations.html). Source data is FM24-era and " +
        "unverified against FM26: treat it as a starting point and correct it against your own save. " +
        "Provided as-is with no warranty of accuracy. Football Manager is a trademark of Sports " +
        "Interactive / SEGA. This project is unofficial and not affiliated with, endorsed by or " +
        "sponsored by Sports Interactive, SEGA, Passion4FM or FM Scout.";

    /// <summary>
    /// Loads the workbook, seeding it first if it is absent. Never throws for a
    /// bad file: a corrupt spreadsheet falls back to the embedded seed and says so.
    /// </summary>
    public static CatalogLoadResult LoadOrSeed(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            try
            {
                Write(path, SeedCatalog.Load());
                var seeded = Read(path);
                return seeded with { Source = CatalogSource.SeedBecauseMissing };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CatalogLoadResult(
                    SeedCatalog.Load(),
                    [$"Could not create '{path}' ({ex.Message}). Using the built-in dataset for this session."],
                    CatalogSource.SeedBecauseMissing);
            }
        }

        try
        {
            return Read(path);
        }
        catch (Exception ex)
        {
            return new CatalogLoadResult(
                SeedCatalog.Load(),
                [$"Could not read '{path}' ({ex.Message}). Using the built-in dataset instead; " +
                 "fix or delete the file and press Reload."],
                CatalogSource.SeedBecauseUnreadable);
        }
    }

    public static CatalogLoadResult Read(string path)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheets.FirstOrDefault(w =>
                        string.Equals(w.Name, DataSheetName, StringComparison.OrdinalIgnoreCase))
                    ?? workbook.Worksheets.First();

        var warnings = new List<string>();
        var rules = new List<CountryRule>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var headerRow = sheet.FirstRowUsed();
        if (headerRow is null)
        {
            return new CatalogLoadResult([], ["The Countries sheet is empty."], CatalogSource.Workbook);
        }

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in headerRow.CellsUsed())
        {
            columns[cell.GetString().Trim()] = cell.Address.ColumnNumber;
        }

        foreach (string required in new[] { "Code", "Country", "StartMonth", "StartDay" })
        {
            if (!columns.ContainsKey(required))
            {
                return new CatalogLoadResult([],
                    [$"Required column '{required}' is missing. Expected headers: {string.Join(", ", Headers)}."],
                    CatalogSource.Workbook);
            }
        }

        foreach (var row in sheet.RowsUsed().Skip(1))
        {
            string code = Text(row, columns, "Code");
            if (code.Length == 0) continue;

            if (!seenCodes.Add(code))
            {
                warnings.Add($"Row {row.RowNumber()}: duplicate code '{code}' ignored.");
                continue;
            }

            int? startMonth = Number(row, columns, "StartMonth");
            int? startDay = Number(row, columns, "StartDay");
            if (startMonth is null || startDay is null)
            {
                warnings.Add($"Row {row.RowNumber()} ('{code}'): missing or non-numeric start date; skipped.");
                continue;
            }

            if (startMonth is < 1 or > 12 || startDay is < 1 or > 31)
            {
                warnings.Add($"Row {row.RowNumber()} ('{code}'): start date {startDay}/{startMonth} is out of range; skipped.");
                continue;
            }

            rules.Add(new CountryRule
            {
                Code = code,
                Country = Fallback(Text(row, columns, "Country"), code),
                Continent = Fallback(Text(row, columns, "Continent"), "Other"),
                YouthRating = Number(row, columns, "YouthRating"),
                WindowStartMonth = startMonth.Value,
                WindowStartDay = startDay.Value,
                // A blank end simply means a single-day window.
                WindowEndMonth = Number(row, columns, "EndMonth") ?? startMonth.Value,
                WindowEndDay = Number(row, columns, "EndDay") ?? startDay.Value,
                Confidence = SeedCatalog.Confidence(Text(row, columns, "Confidence")),
                EnabledByDefault = Flag(row, columns, "EnabledByDefault"),
                Notes = Text(row, columns, "Notes"),
            });
        }

        if (rules.Count == 0) warnings.Add("No usable rows found in the Countries sheet.");

        return new CatalogLoadResult(rules, warnings, CatalogSource.Workbook);
    }

    public static void Write(string path, IReadOnlyList<CountryRule> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(rules);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(DataSheetName);

        for (int i = 0; i < Headers.Length; i++)
        {
            sheet.Cell(1, i + 1).Value = Headers[i];
        }
        sheet.Row(1).Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);

        int r = 2;
        foreach (var rule in rules)
        {
            sheet.Cell(r, 1).Value = rule.Code;
            sheet.Cell(r, 2).Value = rule.Country;
            sheet.Cell(r, 3).Value = rule.Continent;
            if (rule.YouthRating is { } rating) sheet.Cell(r, 4).Value = rating;
            sheet.Cell(r, 5).Value = rule.WindowStartMonth;
            sheet.Cell(r, 6).Value = rule.WindowStartDay;
            sheet.Cell(r, 7).Value = rule.WindowEndMonth;
            sheet.Cell(r, 8).Value = rule.WindowEndDay;
            sheet.Cell(r, 9).Value = rule.Confidence.ToString();
            sheet.Cell(r, 10).Value = rule.EnabledByDefault;
            sheet.Cell(r, 11).Value = rule.Notes;
            r++;
        }

        // Attribution lives on its own sheet rather than in a banner row above the
        // header: a merged banner breaks header detection and every range read.
        var sources = workbook.AddWorksheet(SourcesSheetName);
        sources.Cell(1, 1).Value = "Attribution";
        sources.Cell(1, 1).Style.Font.Bold = true;
        sources.Cell(2, 1).Value = Attribution;
        sources.Cell(2, 1).Style.Alignment.WrapText = true;
        sources.Column(1).Width = 120;
        sources.Cell(4, 1).Value = "Editing this file";
        sources.Cell(4, 1).Style.Font.Bold = true;
        sources.Cell(5, 1).Value =
            "Edit the Countries sheet to adapt this tool to another Football Manager edition. " +
            "Code must be unique and is what your selections are saved against. Leave EndMonth/EndDay " +
            "blank for a single-day window. Delete the file to regenerate the built-in dataset.";
        sources.Cell(5, 1).Style.Alignment.WrapText = true;

        // Explicit widths rather than AdjustToContents(): the latter measures text
        // through SixLabors.Fonts, which throws on CI runners with a thin font set.
        sheet.Column(1).Width = 9;
        sheet.Column(2).Width = 28;
        sheet.Column(3).Width = 16;
        sheet.Column(4).Width = 12;
        for (int c = 5; c <= 8; c++) sheet.Column(c).Width = 11;
        sheet.Column(9).Width = 12;
        sheet.Column(10).Width = 17;
        sheet.Column(11).Width = 70;

        workbook.SaveAs(path);
    }

    private static string Text(IXLRow row, Dictionary<string, int> columns, string name) =>
        columns.TryGetValue(name, out int c) ? row.Cell(c).GetString().Trim() : string.Empty;

    private static int? Number(IXLRow row, Dictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out int c)) return null;
        var cell = row.Cell(c);
        if (cell.IsEmpty()) return null;
        if (cell.DataType == XLDataType.Number) return (int)cell.GetDouble();
        return SeedCatalog.Int(cell.GetString());
    }

    private static bool Flag(IXLRow row, Dictionary<string, int> columns, string name)
    {
        if (!columns.TryGetValue(name, out int c)) return false;
        var cell = row.Cell(c);
        if (cell.IsEmpty()) return false;
        if (cell.DataType == XLDataType.Boolean) return cell.GetBoolean();
        string s = cell.GetString().Trim();
        return bool.TryParse(s, out bool b)
            ? b
            : s is "1" or "yes" or "YES" or "Yes" or "y" or "Y";
    }

    private static string Fallback(string value, string fallback) =>
        value.Length > 0 ? value : fallback;
}
