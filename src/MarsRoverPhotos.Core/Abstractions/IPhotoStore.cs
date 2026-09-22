using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Core.Abstractions;

/// <summary>Owns the on-disk layout: photos/yyyy-MM-dd/&lt;id&gt;_&lt;camera&gt;.jpg</summary>
public interface IPhotoStore
{
    string GetFolderPath(DateOnly earthDate);

    string GetPhotoPath(MarsPhoto photo);

    /// <summary>True when the file already exists and is non-empty, so it must not be downloaded again.</summary>
    bool Exists(MarsPhoto photo);

    /// <summary>Writes to a temp file first, then moves it into place, so a partial write is never mistaken for a cached photo.</summary>
    Task<string> SaveAsync(MarsPhoto photo, Stream content, CancellationToken cancellationToken = default);
}
