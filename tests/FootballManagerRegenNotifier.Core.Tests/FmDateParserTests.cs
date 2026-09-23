using FootballManagerRegenNotifier.Core.Parsing;
using Xunit;

namespace FootballManagerRegenNotifier.Core.Tests;

public class FmDateParserTests
{
    [Theory]
    [InlineData("01/07/2026", 2026, 7, 1)]
    [InlineData("14/03/2026", 2026, 3, 14)]
    [InlineData("1/7/2026", 2026, 7, 1)]
    public void DayFirst_ParsesPlainDates(string raw, int y, int m, int d)
    {
        var result = FmDateParser.Parse(raw, new DateParseOptions { AutoLearnOrder = false });

        Assert.True(result.Success);
        Assert.Equal(new DateOnly(y, m, d), result.Date);
    }

    [Theory]
    [InlineData("01 / 07 / 2026", "01/07/2026")]
    [InlineData("01-07-2026", "01/07/2026")]
    [InlineData("01.07.2026", "01/07/2026")]
    [InlineData("  01/07/2026  ", "01/07/2026")]
    [InlineData("01//07//2026", "01/07/2026")]
    [InlineData("Sat 01/07/2026", "01/07/2026")]
    [InlineData("01/07/2026/", "01/07/2026")]
    public void Normalise_StripsOcrNoise(string raw, string expected)
    {
        Assert.Equal(expected, FmDateParser.Normalise(raw));
    }

    [Fact]
    public void Normalise_ReturnsEmptyForGarbage()
    {
        Assert.Equal(string.Empty, FmDateParser.Normalise("~~~"));
        Assert.Equal(string.Empty, FmDateParser.Normalise(null));
        Assert.Equal(string.Empty, FmDateParser.Normalise("   "));
    }

    [Fact]
    public void WrongComponentCount_FailsCleanly()
    {
        var result = FmDateParser.Parse("01/07");

        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void AutoLearn_LocksDayFirstWhenLeadingComponentExceedsTwelve()
    {
        var result = FmDateParser.Parse("17/03/2026", new DateParseOptions
        {
            Preferred = DateOrder.MonthFirst,
            AutoLearnOrder = true,
        });

        Assert.Equal(DateOrder.DayFirst, result.LearnedOrder);
        Assert.Equal(new DateOnly(2026, 3, 17), result.Date);
    }

    [Fact]
    public void AutoLearn_LocksMonthFirstWhenSecondComponentExceedsTwelve()
    {
        var result = FmDateParser.Parse("03/17/2026", new DateParseOptions
        {
            Preferred = DateOrder.DayFirst,
            AutoLearnOrder = true,
        });

        Assert.Equal(DateOrder.MonthFirst, result.LearnedOrder);
        Assert.Equal(new DateOnly(2026, 3, 17), result.Date);
    }

    [Fact]
    public void AutoLearn_LeavesAmbiguousDatesToTheExistingSetting()
    {
        var result = FmDateParser.Parse("03/04/2026", new DateParseOptions
        {
            Preferred = DateOrder.DayFirst,
            AutoLearnOrder = true,
            LearnedOrder = DateOrder.DayFirst,
        });

        Assert.Equal(new DateOnly(2026, 4, 3), result.Date);
    }

    [Fact]
    public void ExplicitOrder_IsNotOverriddenByAStaleLearnedValue()
    {
        // A user who turns auto-learn off to force day-first must get day-first,
        // even when a month-first value is still persisted from another save.
        var result = FmDateParser.Parse("03/07/2026", new DateParseOptions
        {
            Preferred = DateOrder.DayFirst,
            AutoLearnOrder = false,
            LearnedOrder = DateOrder.MonthFirst,
        });

        Assert.Equal(new DateOnly(2026, 7, 3), result.Date);
    }

    [Fact]
    public void YearFirst_IsDetectedFromAFourDigitLeadingComponent()
    {
        var result = FmDateParser.Parse("2026/03/17", new DateParseOptions { AutoLearnOrder = true });

        Assert.Equal(DateOrder.YearFirst, result.LearnedOrder);
        Assert.Equal(new DateOnly(2026, 3, 17), result.Date);
    }

    [Fact]
    public void ImpossibleDate_FailsRatherThanRolling()
    {
        var result = FmDateParser.Parse("32/03/2026", new DateParseOptions { AutoLearnOrder = false });

        Assert.False(result.Success);
    }

    [Fact]
    public void SevenMisreadAsOne_StillParses_WhichIsWhyConfidenceGatingMatters()
    {
        // Documents the failure mode the preprocessing pipeline exists to prevent:
        // a 7-as-1 misread produces a perfectly valid date, so nothing downstream
        // of the parser can detect it. The defence has to be upstream.
        var wrong = FmDateParser.Parse("01/01/2026", new DateParseOptions { AutoLearnOrder = false });

        Assert.True(wrong.Success);
        Assert.Equal(new DateOnly(2026, 1, 1), wrong.Date);
    }
}
