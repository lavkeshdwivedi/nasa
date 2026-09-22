using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Core.Dates;

/// <summary>
/// Reads dates.txt line by line, keeping the true line numbers so that any later error message
/// points at the place in the file a human would look.
/// </summary>
public sealed class DateFileReader : IDateFileReader
{
    private const char CommentMarker = '#';

    public async Task<IReadOnlyList<RawDateLine>> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            // The absolute path is worth the extra call: a relative path in the message would
            // leave the reader guessing which working directory it was resolved against.
            throw new FileNotFoundException(
                $"Could not find the dates file at '{Path.GetFullPath(filePath)}'. Check the Input:DatesFilePath setting or pass the path on the command line.",
                filePath);
        }

        var lines = new List<RawDateLine>();

        // Streamed rather than ReadAllLinesAsync so a large file never has to sit in memory at
        // once, and so cancellation can take effect between lines.
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };

        await using var stream = new FileStream(filePath, options);
        using var reader = new StreamReader(stream);

        var lineNumber = 0;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Counted before any skipping, so the number always matches the physical file.
            lineNumber++;

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == CommentMarker)
            {
                continue;
            }

            lines.Add(new RawDateLine(lineNumber, trimmed));
        }

        return lines;
    }
}
