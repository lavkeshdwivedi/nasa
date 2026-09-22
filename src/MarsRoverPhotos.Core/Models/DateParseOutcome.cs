namespace MarsRoverPhotos.Core.Models;

/// <summary>
/// The result of parsing one input line. Invalid input is modelled as data rather than
/// an exception so a single bad line never aborts the run.
/// </summary>
public sealed record DateParseOutcome(int LineNumber, string RawValue, DateOnly? Date, string? Error)
{
    public bool IsValid => Date.HasValue;

    public static DateParseOutcome Success(int lineNumber, string rawValue, DateOnly date) =>
        new(lineNumber, rawValue, date, null);

    public static DateParseOutcome Failure(int lineNumber, string rawValue, string error) =>
        new(lineNumber, rawValue, null, error);
}
