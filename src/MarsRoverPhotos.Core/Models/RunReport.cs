namespace MarsRoverPhotos.Core.Models;

/// <summary>Per-date summary shown in the console output and returned by the JSON endpoint.</summary>
public sealed record DateResult(
    int LineNumber,
    string RawInput,
    DateOnly? EarthDate,
    bool IsValid,
    int PhotosDownloaded,
    int PhotosSkipped,
    int PhotosFailed,
    string? FolderPath,
    IReadOnlyList<string> Errors)
{
    public int PhotosAvailable => PhotosDownloaded + PhotosSkipped + PhotosFailed;
}

public sealed record RunReport(
    string Rover,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<DateResult> Results)
{
    public int TotalDatesProcessed => Results.Count;
    public int TotalValidDates => Results.Count(r => r.IsValid);
    public int TotalInvalidDates => Results.Count(r => !r.IsValid);
    public int TotalPhotosDownloaded => Results.Sum(r => r.PhotosDownloaded);
    public int TotalPhotosSkipped => Results.Sum(r => r.PhotosSkipped);
    public int TotalPhotosFailed => Results.Sum(r => r.PhotosFailed);
    public TimeSpan Duration => CompletedAt - StartedAt;
}
