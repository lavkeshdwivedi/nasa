namespace MarsRoverPhotos.Core.Options;

public sealed class NasaOptions
{
    public const string SectionName = "Nasa";

    /// <summary>Never hardcoded: read from configuration, user secrets or the NASA_API_KEY environment variable.</summary>
    public string ApiKey { get; set; } = "DEMO_KEY";

    public string BaseAddress { get; set; } = "https://api.nasa.gov/mars-photos/api/v1/";

    public string Rover { get; set; } = "curiosity";

    public int MaxPhotosPerDate { get; set; } = 5;

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 3;
}
