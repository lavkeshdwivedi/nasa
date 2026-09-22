using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Cli;

/// <summary>
/// Renders a <see cref="RunReport"/> as an aligned plain text table. Deliberately has no
/// package dependency: the whole point of the report is to be readable in any terminal,
/// including one that is being piped into a file.
/// </summary>
public sealed class ConsoleReportWriter
{
    private const string InvalidLabel = "INVALID";
    private const string NoFolder = "-";

    private readonly TextWriter _output;
    private readonly bool _useColour;

    public ConsoleReportWriter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;

        // NO_COLOR is honoured for any non-empty value, per the no-color.org convention.
        // Redirected output is almost always a file or a pipe, where escape codes are litter.
        var noColour = Environment.GetEnvironmentVariable("NO_COLOR");
        _useColour = string.IsNullOrEmpty(noColour) && !Console.IsOutputRedirected;
    }

    public void Write(RunReport report, string? outputRoot = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        _output.WriteLine();
        _output.WriteLine($"Mars rover photo run: {report.Rover}");
        _output.WriteLine($"Started {report.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        _output.WriteLine($"Output folder: {outputRoot ?? InferOutputRoot(report) ?? "(none)"}");
        _output.WriteLine();

        WriteTable(report);

        _output.WriteLine();
        WriteTotals(report);
    }

    private void WriteTable(RunReport report)
    {
        // Widths are measured from the data so the table never wraps on a short report and
        // never wastes half the terminal on a long one.
        var lineWidth = Math.Max(2, report.Results.Count == 0 ? 2 : report.Results.Max(r => r.LineNumber.ToString().Length));
        var inputWidth = Width("INPUT", report.Results.Select(r => r.RawInput));
        var parsedWidth = Width("PARSED", report.Results.Select(FormatParsed));
        var folderWidth = Width("FOLDER", report.Results.Select(r => r.FolderPath ?? NoFolder));

        const int CountWidth = 10;

        var header = string.Concat(
            "#".PadLeft(lineWidth),
            "  ",
            "INPUT".PadRight(inputWidth),
            "  ",
            "PARSED".PadRight(parsedWidth),
            "  ",
            "DOWNLOADED".PadLeft(CountWidth),
            "  ",
            "SKIPPED".PadLeft(7),
            "  ",
            "FAILED".PadLeft(6),
            "  ",
            "FOLDER".PadRight(folderWidth));

        _output.WriteLine(header);
        _output.WriteLine(new string('-', header.TrimEnd().Length));

        if (report.Results.Count == 0)
        {
            WithColour(ConsoleColor.Yellow, () => _output.WriteLine("(no dates were read from the input file)"));
            return;
        }

        foreach (var result in report.Results)
        {
            var row = string.Concat(
                result.LineNumber.ToString().PadLeft(lineWidth),
                "  ",
                result.RawInput.PadRight(inputWidth),
                "  ",
                FormatParsed(result).PadRight(parsedWidth),
                "  ",
                result.PhotosDownloaded.ToString().PadLeft(CountWidth),
                "  ",
                result.PhotosSkipped.ToString().PadLeft(7),
                "  ",
                result.PhotosFailed.ToString().PadLeft(6),
                "  ",
                (result.FolderPath ?? NoFolder).PadRight(folderWidth));

            var colour = ColourFor(result);
            WithColour(colour, () => _output.WriteLine(row.TrimEnd()));

            // Errors go underneath rather than in a column: they are prose, and squeezing
            // them into the grid would force every other column to shrink.
            var indent = new string(' ', lineWidth + 2);
            foreach (var error in result.Errors)
            {
                WithColour(ConsoleColor.Red, () => _output.WriteLine($"{indent}! {error}"));
            }
        }
    }

    private void WriteTotals(RunReport report)
    {
        var totals =
            $"Totals: {report.TotalDatesProcessed} dates, " +
            $"{report.TotalValidDates} valid, " +
            $"{report.TotalInvalidDates} invalid, " +
            $"{report.TotalPhotosDownloaded} downloaded, " +
            $"{report.TotalPhotosSkipped} already present, " +
            $"{report.TotalPhotosFailed} failed";

        var clean = report.TotalInvalidDates == 0
                    && report.TotalPhotosFailed == 0
                    && report.Results.All(r => r.Errors.Count == 0);

        var totalsColour = clean
            ? ConsoleColor.Green
            : report.TotalInvalidDates > 0 || report.TotalPhotosFailed > 0
                ? ConsoleColor.Red
                : ConsoleColor.Yellow;

        WithColour(totalsColour, () => _output.WriteLine(totals));
        _output.WriteLine($"Elapsed: {FormatDuration(report.Duration)}");
    }

    private static string FormatParsed(DateResult result) =>
        result.IsValid && result.EarthDate is { } date
            ? date.ToString("yyyy-MM-dd")
            : InvalidLabel;

    private static ConsoleColor ColourFor(DateResult result)
    {
        if (!result.IsValid || result.Errors.Count > 0 || result.PhotosFailed > 0)
        {
            return ConsoleColor.Red;
        }

        // Nothing went wrong, but nothing new arrived either: worth a second look, not an alarm.
        if (result.PhotosDownloaded == 0)
        {
            return ConsoleColor.Yellow;
        }

        return ConsoleColor.Green;
    }

    private static int Width(string header, IEnumerable<string> values) =>
        Math.Max(header.Length, values.Select(v => v.Length).DefaultIfEmpty(0).Max());

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? $"{duration.TotalMilliseconds:F0} ms"
            : duration.TotalMinutes < 1
                ? $"{duration.TotalSeconds:F2} s"
                : $"{duration:hh\\:mm\\:ss}";

    /// <summary>Best effort fallback when the caller does not pass the configured root.</summary>
    private static string? InferOutputRoot(RunReport report)
    {
        var folder = report.Results.Select(r => r.FolderPath).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        return folder is null ? null : Path.GetDirectoryName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    /// <summary>
    /// Restores whatever colour the host had, not a hardcoded default, so the CLI leaves a
    /// themed terminal exactly as it found it even if a write throws.
    /// </summary>
    private void WithColour(ConsoleColor colour, Action write)
    {
        if (!_useColour)
        {
            write();
            return;
        }

        var previous = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = colour;
            write();
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }
}
