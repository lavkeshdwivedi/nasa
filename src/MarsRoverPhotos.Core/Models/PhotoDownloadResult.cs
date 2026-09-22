namespace MarsRoverPhotos.Core.Models;

public enum PhotoDownloadStatus
{
    Downloaded,
    Skipped,
    Failed
}

public sealed record PhotoDownloadResult(
    MarsPhoto Photo,
    PhotoDownloadStatus Status,
    string? LocalPath,
    string? Error)
{
    public static PhotoDownloadResult Downloaded(MarsPhoto photo, string localPath) =>
        new(photo, PhotoDownloadStatus.Downloaded, localPath, null);

    public static PhotoDownloadResult Skipped(MarsPhoto photo, string localPath) =>
        new(photo, PhotoDownloadStatus.Skipped, localPath, null);

    public static PhotoDownloadResult Failed(MarsPhoto photo, string error) =>
        new(photo, PhotoDownloadStatus.Failed, null, error);
}
