using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Core.Abstractions;

public interface IMarsPhotoClient
{
    /// <summary>
    /// Fetches up to <paramref name="maxPhotos"/> photos for the given earth date.
    /// Throws <see cref="MarsPhotoClientException"/> when the API cannot be reached or returns an error.
    /// </summary>
    Task<IReadOnlyList<MarsPhoto>> GetPhotosAsync(
        DateOnly earthDate,
        string rover,
        int maxPhotos,
        CancellationToken cancellationToken = default);
}

public sealed class MarsPhotoClientException : Exception
{
    public MarsPhotoClientException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
