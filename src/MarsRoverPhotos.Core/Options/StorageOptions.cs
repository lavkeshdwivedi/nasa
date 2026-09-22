namespace MarsRoverPhotos.Core.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string RootPath { get; set; } = "photos";

    /// <summary>How many photos to download at the same time within one date.</summary>
    public int MaxParallelDownloads { get; set; } = 4;
}
