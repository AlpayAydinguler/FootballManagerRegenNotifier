using FootballManagerRegenNotifier.Core.Catalog;
using FootballManagerRegenNotifier.Core.Model;
using Xunit;

namespace FootballManagerRegenNotifier.Core.Tests;

public class SeedCatalogTests
{
    [Fact]
    public void Seed_LoadsEveryRow()
    {
        var rules = SeedCatalog.Load();

        Assert.Equal(91, rules.Count);
    }

    [Fact]
    public void Seed_HasNoDuplicateCodes()
    {
        var rules = SeedCatalog.Load();

        var duplicates = rules.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void Seed_HasValidCalendarRangesEverywhere()
    {
        foreach (var r in SeedCatalog.Load())
        {
            Assert.InRange(r.WindowStartMonth, 1, 12);
            Assert.InRange(r.WindowEndMonth, 1, 12);
            Assert.InRange(r.WindowStartDay, 1, DateTime.DaysInMonth(2027, r.WindowStartMonth));
            Assert.InRange(r.WindowEndDay, 1, DateTime.DaysInMonth(2027, r.WindowEndMonth));
        }
    }

    [Fact]
    public void Seed_TopTierThresholdSelectsExactlyTheNineEliteNations()
    {
        // The 130 cut sits inside a seven-point gap (Netherlands 132 to Egypt 125),
        // so it is stable against small rating revisions. If this test starts
        // failing, the ratings changed and the default threshold wants revisiting.
        var selected = SeedCatalog.Load()
            .Where(r => r.YouthRating >= 130 && !r.IsLowerLeague)
            .Select(r => r.Country)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["Argentina", "Brazil", "England", "France", "Germany", "Italy", "Netherlands", "Portugal", "Spain"],
            selected);
    }

    [Fact]
    public void Seed_TopTierSelectionContainsNoLowConfidenceRows()
    {
        // A one-click bulk action must never quietly arm data we know is stale.
        var selected = SeedCatalog.Load().Where(r => r.YouthRating >= 130 && !r.IsLowerLeague);

        Assert.All(selected, r => Assert.Equal(DataConfidence.High, r.Confidence));
    }

    [Fact]
    public void Seed_LowerLeagueRowsAreFlaggedByCode()
    {
        var lowerLeague = SeedCatalog.Load().Where(r => r.IsLowerLeague).ToList();

        Assert.Equal(10, lowerLeague.Count);
        Assert.All(lowerLeague, r => Assert.EndsWith("(Lower Leagues)", r.Country, StringComparison.Ordinal));
    }

    [Fact]
    public void Seed_PreservesNonAsciiCountryNames()
    {
        // A mangled encoding silently forks Turkiye into a second country row,
        // which is exactly the join bug this dataset was rebuilt to remove.
        var rules = SeedCatalog.Load();

        Assert.Contains(rules, r => r.Code == "TUR" && r.Country == "Türkiye");
        Assert.Contains(rules, r => r.Code == "TUR-LL");
    }

    [Fact]
    public void Seed_NoRowActuallyCrossesTheYearBoundary()
    {
        // Documents a real property of the shipped data. The wrap-around branch in
        // TriggerCalendar exists for user-edited files, not for anything here.
        Assert.DoesNotContain(SeedCatalog.Load(), r => r.CrossesYearBoundary);
    }

    [Fact]
    public void Seed_EnablesASensibleDefaultSelection()
    {
        var enabled = SeedCatalog.Load().Where(r => r.EnabledByDefault).ToList();

        Assert.Equal(9, enabled.Count);
        Assert.All(enabled, r => Assert.True(r.YouthRating >= 130));
    }
}

public class CountryCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "fmrn-tests-" + Guid.NewGuid().ToString("N"));

    private string Path_(string name) => Path.Combine(_dir, name);

    public CountryCatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void MissingFile_IsSeededAndThenReadBack()
    {
        string path = Path_("countries.xlsx");

        var result = CountryCatalog.LoadOrSeed(path);

        Assert.True(File.Exists(path));
        Assert.Equal(CatalogSource.SeedBecauseMissing, result.Source);
        Assert.Equal(91, result.Rules.Count);
        Assert.False(result.HasWarnings);
    }

    [Fact]
    public void WriteThenRead_RoundTripsEveryField()
    {
        string path = Path_("roundtrip.xlsx");
        var original = SeedCatalog.Load();

        CountryCatalog.Write(path, original);
        var read = CountryCatalog.Read(path).Rules;

        Assert.Equal(original.Count, read.Count);

        var a = original.Single(r => r.Code == "MEX");
        var b = read.Single(r => r.Code == "MEX");
        Assert.Equal(a.Country, b.Country);
        Assert.Equal(a.Continent, b.Continent);
        Assert.Equal(a.YouthRating, b.YouthRating);
        Assert.Equal(a.WindowStartMonth, b.WindowStartMonth);
        Assert.Equal(a.WindowStartDay, b.WindowStartDay);
        Assert.Equal(a.WindowEndMonth, b.WindowEndMonth);
        Assert.Equal(a.WindowEndDay, b.WindowEndDay);
        Assert.Equal(a.Confidence, b.Confidence);
        Assert.Equal(a.Notes, b.Notes);
    }

    [Fact]
    public void RoundTrip_PreservesNonAsciiNames()
    {
        string path = Path_("unicode.xlsx");
        CountryCatalog.Write(path, SeedCatalog.Load());

        var read = CountryCatalog.Read(path).Rules;

        Assert.Equal("Türkiye", read.Single(r => r.Code == "TUR").Country);
    }

    [Fact]
    public void RoundTrip_PreservesTheDefaultSelection()
    {
        string path = Path_("enabled.xlsx");
        CountryCatalog.Write(path, SeedCatalog.Load());

        var read = CountryCatalog.Read(path).Rules;

        Assert.Equal(9, read.Count(r => r.EnabledByDefault));
    }

    [Fact]
    public void CorruptFile_FallsBackToTheSeedInsteadOfThrowing()
    {
        string path = Path_("corrupt.xlsx");
        File.WriteAllText(path, "this is definitely not a workbook");

        var result = CountryCatalog.LoadOrSeed(path);

        Assert.Equal(CatalogSource.SeedBecauseUnreadable, result.Source);
        Assert.NotEmpty(result.Rules);
        Assert.True(result.HasWarnings);
    }

    [Fact]
    public void BlankEndDate_MeansASingleDayWindow()
    {
        string path = Path_("blank-end.xlsx");
        CountryCatalog.Write(path, [TestData.Rule("AAA", 3, 14, 3, 14)]);

        var rule = CountryCatalog.Read(path).Rules.Single();

        Assert.Equal(3, rule.WindowEndMonth);
        Assert.Equal(14, rule.WindowEndDay);
    }

    [Fact]
    public void OutOfRangeRow_IsSkippedWithAWarningAndTheRestSurvive()
    {
        string path = Path_("bad-row.xlsx");
        CountryCatalog.Write(path,
        [
            TestData.Rule("GOOD", 3, 14, 3, 20),
            TestData.Rule("BAD", 13, 40, 3, 20),
        ]);

        var result = CountryCatalog.Read(path);

        Assert.Single(result.Rules);
        Assert.Equal("GOOD", result.Rules[0].Code);
        Assert.Contains(result.Warnings, w => w.Contains("BAD", StringComparison.Ordinal));
    }

    [Fact]
    public void WrittenWorkbook_CarriesTheAttributionSheet()
    {
        string path = Path_("attribution.xlsx");
        CountryCatalog.Write(path, SeedCatalog.Load());

        using var workbook = new ClosedXML.Excel.XLWorkbook(path);

        Assert.True(workbook.Worksheets.Contains(CountryCatalog.SourcesSheetName));
        Assert.Contains("Passion4FM",
            workbook.Worksheet(CountryCatalog.SourcesSheetName).Cell(2, 1).GetString(),
            StringComparison.Ordinal);
    }
}
