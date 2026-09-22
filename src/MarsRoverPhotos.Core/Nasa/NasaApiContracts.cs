using System.Text.Json.Serialization;

namespace MarsRoverPhotos.Core.Nasa;

/// <summary>
/// Raw wire shapes for the NASA Mars Rover Photos API. These mirror the JSON exactly and are
/// deliberately separate from <see cref="Models.MarsPhoto"/> so a change at NASA's end does not
/// leak into the domain model. Property names are spelled out with
/// <see cref="JsonPropertyNameAttribute"/> rather than relying on a naming policy, so a reviewer
/// can see the wire contract without knowing the serializer configuration.
/// </summary>
internal sealed class NasaPhotosResponse
{
    [JsonPropertyName("photos")]
    public List<NasaPhotoDto>? Photos { get; set; }
}

internal sealed class NasaPhotoDto
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("img_src")]
    public string? ImgSrc { get; set; }

    /// <summary>Kept as a string: NASA sends "yyyy-MM-dd" and we parse it with an exact format.</summary>
    [JsonPropertyName("earth_date")]
    public string? EarthDate { get; set; }

    [JsonPropertyName("camera")]
    public NasaCameraDto? Camera { get; set; }

    [JsonPropertyName("rover")]
    public NasaRoverDto? Rover { get; set; }
}

internal sealed class NasaCameraDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }
}

internal sealed class NasaRoverDto
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}
