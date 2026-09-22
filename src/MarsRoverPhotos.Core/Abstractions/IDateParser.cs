using MarsRoverPhotos.Core.Models;

namespace MarsRoverPhotos.Core.Abstractions;

public interface IDateParser
{
    /// <summary>Parses one line into a date. Never throws for bad input.</summary>
    DateParseOutcome Parse(RawDateLine line);
}
