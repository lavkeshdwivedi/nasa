using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Core.Abstractions;

public interface IPhotoPipeline
{
    /// <summary>Reads the dates file, then runs the full parse, fetch and download flow.</summary>
    Task<RunReport> RunAsync(string? datesFilePath = null, CancellationToken cancellationToken = default);

    /// <summary>Same flow for dates supplied in memory, used by the API and by tests.</summary>
    Task<RunReport> RunAsync(IReadOnlyList<RawDateLine> lines, CancellationToken cancellationToken = default);
}
