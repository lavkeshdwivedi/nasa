using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Core.Abstractions;

public interface IDateFileReader
{
    /// <summary>Reads the input file, skipping blank lines and trimming whitespace.</summary>
    Task<IReadOnlyList<RawDateLine>> ReadAsync(string filePath, CancellationToken cancellationToken = default);
}
