namespace MarsRoverPhotos.Core.Models;

/// <summary>A photo returned by the NASA Mars Rover Photos API, flattened to what we need.</summary>
public sealed record MarsPhoto(
    long Id,
    Uri ImageSourceUrl,
    DateOnly EarthDate,
    string RoverName,
    string CameraName);
