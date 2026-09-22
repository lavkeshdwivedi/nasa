using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Core.Abstractions;

public interface IPhotoDownloader
{
    /// <summary>Downloads one photo unless it is already on disk. Never throws for network failures.</summary>
    Task<PhotoDownloadResult> DownloadAsync(MarsPhoto photo, CancellationToken cancellationToken = default);
}
