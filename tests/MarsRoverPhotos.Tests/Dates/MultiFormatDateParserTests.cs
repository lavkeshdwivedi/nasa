using MarsRoverPhotos.Core.Dates;
using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Tests.Dates;

public sealed class MultiFormatDateParserTests
{
    private static readonly DateTimeOffset Today = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("02/27/17", 2017, 2, 27)]
    [InlineData("June 2, 2018", 2018, 6, 2)]
    [InlineData("Jul-13-2016", 2016, 7, 13)]
    [InlineData("6/2/2018", 2018, 6, 2)]
    [InlineData("13-Jul-2016", 2016, 7, 13)]
    [InlineData("2016-07-13", 2016, 7, 13)]
    [InlineData("July-13-2016", 2016, 7, 13)]
    public void Parse_ReadsEverySupportedFormat(string raw, int year, int month, int day)
    {
        var outcome = Parse(raw);

        Assert.True(outcome.IsValid, outcome.Error);
        Assert.Equal(new DateOnly(year, month, day), outcome.Date);
        Assert.Null(outcome.Error);
    }

    [Fact]
    public void Parse_RejectsApril31AsACalendarImpossibility()
    {
        var outcome = Parse("April 31, 2018");

        Assert.False(outcome.IsValid);
        Assert.NotNull(outcome.Error);
        Assert.Contains("April 31, 2018", outcome.Error!);
        Assert.Contains("not a real one", outcome.Error!);
        Assert.Contains("April 2018 has 30 days", outcome.Error!);
    }

    [Fact]
    public void Parse_RejectsImpossibleNumericDateAsACalendarImpossibility()
    {
        var outcome = Parse("02/30/17");

        Assert.False(outcome.IsValid);
        Assert.Contains("February 2017 has 28 days", outcome.Error!);
    }

    [Fact]
    public void Parse_RejectsUnrecognisedValueWithoutBlamingTheCalendar()
    {
        var outcome = Parse("last tuesday-ish");

        Assert.False(outcome.IsValid);
        Assert.Contains("last tuesday-ish", outcome.Error!);
        Assert.Contains("not in a recognised date format", outcome.Error!);
    }

    [Fact]
    public void Parse_RejectsFutureDateAgainstTheInjectedClock()
    {
        var parser = new MultiFormatDateParser(new FixedTimeProvider(new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var outcome = parser.Parse(new RawDateLine(1, "Jul-13-2016"));

        Assert.False(outcome.IsValid);
        Assert.Contains("in the future", outcome.Error!);
        Assert.Contains("2016-01-01", outcome.Error!);
    }

    [Fact]
    public void Parse_AcceptsTodayItself()
    {
        var outcome = Parse("09/22/26");

        Assert.True(outcome.IsValid, outcome.Error);
        Assert.Equal(new DateOnly(2026, 9, 22), outcome.Date);
    }

    [Fact]
    public void Parse_TreatsTwoDigitYearsAsTwentyFirstCentury()
    {
        // 99 means 2099, not 1999, which is why this fails as a future date rather than
        // as a date before the first landing.
        var outcome = Parse("01/02/99");

        Assert.False(outcome.IsValid);
        Assert.Contains("2099-01-02", outcome.Error!);
        Assert.Contains("in the future", outcome.Error!);
    }

    [Fact]
    public void Parse_RejectsDatesBeforeSpiritLanded()
    {
        var outcome = Parse("01/03/2004");

        Assert.False(outcome.IsValid);
        Assert.Contains("2004-01-04", outcome.Error!);
    }

    [Fact]
    public void Parse_AcceptsTheLandingDayItself()
    {
        var outcome = Parse("01/04/2004");

        Assert.True(outcome.IsValid, outcome.Error);
        Assert.Equal(new DateOnly(2004, 1, 4), outcome.Date);
    }

    [Fact]
    public void Parse_NormalisesSurroundingAndRepeatedWhitespace()
    {
        var outcome = Parse("   June    2,\t2018  ");

        Assert.True(outcome.IsValid, outcome.Error);
        Assert.Equal(new DateOnly(2018, 6, 2), outcome.Date);
    }

    [Fact]
    public void Parse_ReportsBlankValueWithoutThrowing()
    {
        var outcome = Parse("   ");

        Assert.False(outcome.IsValid);
        Assert.Contains("blank", outcome.Error!);
    }

    [Fact]
    public void Parse_KeepsTheLineNumberAndOriginalValueOnFailure()
    {
        var parser = CreateParser();

        var outcome = parser.Parse(new RawDateLine(42, "  April 31, 2018 "));

        Assert.Equal(42, outcome.LineNumber);
        Assert.Equal("  April 31, 2018 ", outcome.RawValue);
    }

    private static DateParseOutcome Parse(string raw) => CreateParser().Parse(new RawDateLine(1, raw));

    private static MultiFormatDateParser CreateParser() => new(new FixedTimeProvider(Today));

    /// <summary>Microsoft.Extensions.TimeProvider.Testing is not referenced, so this stands in.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
