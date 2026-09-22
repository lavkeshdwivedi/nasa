namespace MarsRoverPhotos.Core.Models;

/// <summary>A single non-empty line read from the input file, with its 1-based line number.</summary>
public sealed record RawDateLine(int LineNumber, string Value);
