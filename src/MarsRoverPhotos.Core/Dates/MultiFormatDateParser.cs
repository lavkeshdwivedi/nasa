using System.Globalization;
using System.Text.RegularExpressions;
using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Core.Dates;

/// <summary>
/// Parses the mixed date shapes found in dates.txt against an explicit, ordered list of formats.
/// Bad input is reported as a failed <see cref="DateParseOutcome"/> rather than an exception.
/// </summary>
public sealed partial class MultiFormatDateParser : IDateParser
{
    // The list is deliberate rather than a bare DateTime.TryParse on the ambient culture.
    // TryParse would read "02/03/2017" as 2 March on a machine set to en-GB and as 3 February
    // on one set to en-US, so the same input file would yield different photos depending on who
    // ran it. An explicit list pins the day/month order and keeps runs reproducible anywhere.
    private static readonly string[] Formats =
    [
        // Slash forms in US month/day order, which is what the supplied dates.txt uses.
        "MM/dd/yy", "M/d/yy", "MM/dd/yyyy", "M/d/yyyy",
        // Month name forms, full and abbreviated, comma separated.
        "MMMM d, yyyy", "MMMM dd, yyyy", "MMM d, yyyy", "MMM dd, yyyy",
        // Month name forms joined by hyphens, e.g. Jul-13-2016.
        "MMMM-d-yyyy", "MMMM-dd-yyyy", "MMM-d-yyyy", "MMM-dd-yyyy",
        // Day first is accepted only with a named month, where the order cannot be misread.
        "d-MMM-yyyy", "dd-MMM-yyyy", "d-MMMM-yyyy", "dd-MMMM-yyyy",
        // Hyphenated numeric, same US order as the slash forms above.
        "MM-dd-yyyy", "M-d-yyyy", "MM-dd-yy", "M-d-yy",
        // ISO 8601 and its slash variant, unambiguous by definition.
        "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd"
    ];

    // Spirit landed on 4 January 2004 and is the earliest rover this API serves, so no photo can
    // carry an earlier date. An older floor such as Pathfinder's 1997 landing would let plainly
    // wrong years through as if they were plausible.
    private static readonly DateOnly EarliestSupportedDate = new(2004, 1, 4);

    // Century rule: a two digit year always means 20nn, never 19nn. We do not lean on the
    // framework's TwoDigitYearMax window, because it is a culture setting that can be changed per
    // machine. 20nn is safe in this domain: anything before 2004 is rejected as pre-Spirit and
    // anything after today is rejected as future, so 19nn could never be a valid answer.
    private const int TwoDigitYearCentury = 2000;

    private readonly TimeProvider _timeProvider;

    public MultiFormatDateParser(TimeProvider? timeProvider = null) =>
        _timeProvider = timeProvider ?? TimeProvider.System;

    public DateParseOutcome Parse(RawDateLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var raw = line.Value;
        var normalised = Normalise(raw);

        if (normalised.Length == 0)
        {
            return DateParseOutcome.Failure(line.LineNumber, raw, "The line is blank, so there is no date to read.");
        }

        if (!TryParseExactAny(normalised, out var date))
        {
            return DateParseOutcome.Failure(line.LineNumber, raw, Explain(raw, normalised));
        }

        // The clock is injected so the boundary is testable, and so an old input file can be
        // replayed deterministically against a fixed clock.
        var today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime);

        if (date > today)
        {
            return DateParseOutcome.Failure(
                line.LineNumber,
                raw,
                $"'{raw}' resolves to {date:yyyy-MM-dd}, which is in the future. Today is {today:yyyy-MM-dd}, and NASA has no photos from a day that has not happened yet.");
        }

        if (date < EarliestSupportedDate)
        {
            return DateParseOutcome.Failure(
                line.LineNumber,
                raw,
                $"'{raw}' resolves to {date:yyyy-MM-dd}, which is before {EarliestSupportedDate:yyyy-MM-dd}, the day Spirit landed and the earliest date any rover photo can carry.");
        }

        return DateParseOutcome.Success(line.LineNumber, raw, date);
    }

    /// <summary>Trims the value and collapses runs of internal whitespace to a single space.</summary>
    private static string Normalise(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : WhitespaceRegex().Replace(value.Trim(), " ");

    private static bool TryParseExactAny(string value, out DateOnly date)
    {
        foreach (var format in Formats)
        {
            if (!DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                continue;
            }

            date = DateOnly.FromDateTime(ApplyCenturyRule(parsed, format));
            return true;
        }

        date = default;
        return false;
    }

    private static DateTime ApplyCenturyRule(DateTime parsed, string format) =>
        format.Contains("yyyy", StringComparison.Ordinal)
            ? parsed
            : new DateTime(TwoDigitYearCentury + (parsed.Year % 100), parsed.Month, parsed.Day);

    /// <summary>
    /// Separates the two ways a value can be rejected: a shape we do not recognise at all, and a
    /// shape we do recognise carrying a day the calendar does not have, such as 31 April.
    /// </summary>
    private static string Explain(string raw, string normalised)
    {
        if (TryReadShape(normalised, out var year, out var month, out var day))
        {
            if (month is < 1 or > 12)
            {
                return $"'{raw}' looks like a date, but month {month} does not exist; months run from 1 to 12.";
            }

            var daysInMonth = DateTime.DaysInMonth(year, month);
            if (day < 1 || day > daysInMonth)
            {
                var monthName = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);
                return $"'{raw}' looks like a date, but it is not a real one: {monthName} {year} has {daysInMonth} days, so day {day} does not exist.";
            }
        }

        return $"'{raw}' is not in a recognised date format. Examples we accept: 02/27/17, 6/2/2018, June 2, 2018, Jul-13-2016, 13-Jul-2016, 2016-07-13.";
    }

    /// <summary>
    /// Pulls year, month and day out of a value with a date-like shape, without validating the
    /// combination. Returns false when the value is not date shaped at all.
    /// </summary>
    private static bool TryReadShape(string value, out int year, out int month, out int day)
    {
        year = 0;
        month = 0;
        day = 0;

        var iso = IsoShapeRegex().Match(value);
        if (iso.Success)
        {
            year = ReadNumber(iso.Groups[1].Value);
            month = ReadNumber(iso.Groups[2].Value);
            day = ReadNumber(iso.Groups[3].Value);
            return IsUsableYear(year);
        }

        var numeric = NumericShapeRegex().Match(value);
        if (numeric.Success)
        {
            month = ReadNumber(numeric.Groups[1].Value);
            day = ReadNumber(numeric.Groups[2].Value);
            year = ExpandYear(numeric.Groups[3].Value);
            return IsUsableYear(year);
        }

        var named = MonthNameShapeRegex().Match(value);
        if (named.Success && TryReadMonthName(named.Groups[1].Value, out month))
        {
            day = ReadNumber(named.Groups[2].Value);
            year = ExpandYear(named.Groups[3].Value);
            return IsUsableYear(year);
        }

        return false;
    }

    private static bool TryReadMonthName(string name, out int month)
    {
        var format = CultureInfo.InvariantCulture.DateTimeFormat;

        for (var candidate = 1; candidate <= 12; candidate++)
        {
            if (string.Equals(name, format.GetMonthName(candidate), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, format.GetAbbreviatedMonthName(candidate), StringComparison.OrdinalIgnoreCase))
            {
                month = candidate;
                return true;
            }
        }

        month = 0;
        return false;
    }

    private static int ReadNumber(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    private static int ExpandYear(string value)
    {
        var number = ReadNumber(value);
        return value.Length == 2 ? TwoDigitYearCentury + number : number;
    }

    // DateTime.DaysInMonth only accepts years 1 to 9999, and a year outside that is garbage anyway.
    private static bool IsUsableYear(int year) => year is >= 1 and <= 9999;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(\d{4})[/-](\d{1,2})[/-](\d{1,2})$")]
    private static partial Regex IsoShapeRegex();

    [GeneratedRegex(@"^(\d{1,2})[/-](\d{1,2})[/-](\d{2}|\d{4})$")]
    private static partial Regex NumericShapeRegex();

    [GeneratedRegex(@"^([A-Za-z]+)\.?[\s-]+(\d{1,2}),?[\s-]+(\d{4})$")]
    private static partial Regex MonthNameShapeRegex();
}
