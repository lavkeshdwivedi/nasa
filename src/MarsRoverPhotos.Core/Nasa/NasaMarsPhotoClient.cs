using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Models;
using MarsRoverPhotos.Core.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace MarsRoverPhotos.Core.Nasa;

/// <summary>
/// Talks to the NASA Mars Rover Photos API over a pooled <see cref="HttpClient"/> supplied by
/// IHttpClientFactory. The client is never constructed here: lifetime, retries and timeouts are
/// configured at registration time.
/// </summary>
public sealed class NasaMarsPhotoClient : IMarsPhotoClient
{
    private const string EarthDateFormat = "yyyy-MM-dd";

    // Explicit [JsonPropertyName] attributes carry the wire names, so no naming policy is needed.
    // Case insensitivity is only a cheap tolerance in case NASA ever changes casing.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly NasaOptions _options;
    private readonly ILogger<NasaMarsPhotoClient> _logger;

    public NasaMarsPhotoClient(
        HttpClient httpClient,
        IOptions<NasaOptions> options,
        ILogger<NasaMarsPhotoClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<MarsPhoto>> GetPhotosAsync(
        DateOnly earthDate,
        string rover,
        int maxPhotos,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rover))
        {
            throw new ArgumentException("Rover name must not be empty.", nameof(rover));
        }

        // Asking for nothing is not an error, and there is no point spending a request on it.
        if (maxPhotos <= 0)
        {
            return Array.Empty<MarsPhoto>();
        }

        var requestUri = BuildRequestUri(earthDate, rover);
        var formattedDate = earthDate.ToString(EarthDateFormat, CultureInfo.InvariantCulture);

        NasaPhotosResponse? payload;
        try
        {
            using var response = await _httpClient
                .GetAsync(requestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw Fail(DescribeStatus(response.StatusCode, formattedDate, rover), formattedDate, rover);
            }

            await using var stream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            payload = await JsonSerializer
                .DeserializeAsync<NasaPhotosResponse>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw Fail(
                $"NASA returned a response for {formattedDate} that could not be parsed as JSON.",
                formattedDate,
                rover,
                ex);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // A caller-requested cancellation is not a client failure, so it travels up untouched.
            _logger.LogWarning(
                ex,
                "Fetch of NASA photos for {EarthDate} on rover {Rover} was cancelled",
                formattedDate,
                rover);
            throw;
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient surfaces its own timeout as TaskCanceledException with no cancellation asked for.
            throw Fail(
                $"The NASA request for {formattedDate} timed out after {_options.TimeoutSeconds}s.",
                formattedDate,
                rover,
                ex);
        }
        catch (TimeoutRejectedException ex)
        {
            // The resilience handler's per-attempt timeout throws Polly's own exception type rather
            // than TaskCanceledException, so it needs its own catch or it escapes as an unhandled fault.
            throw Fail(
                $"The NASA request for {formattedDate} timed out after {_options.TimeoutSeconds}s.",
                formattedDate,
                rover,
                ex);
        }
        catch (BrokenCircuitException ex)
        {
            // The resilience handler's circuit breaker trips after repeated failures and short-circuits
            // further attempts; that is still an API-level failure for this one date, not a fatal fault.
            throw Fail(
                $"The NASA API is currently unreachable for {formattedDate} (circuit breaker open after repeated failures).",
                formattedDate,
                rover,
                ex);
        }
        catch (HttpRequestException ex)
        {
            throw Fail(
                $"The NASA request for {formattedDate} failed at the network level: {ex.Message}",
                formattedDate,
                rover,
                ex);
        }

        if (payload is null)
        {
            throw Fail(
                $"NASA returned an empty body for {formattedDate} where a photos object was expected.",
                formattedDate,
                rover);
        }

        // No photos for a date is a normal, frequent answer from NASA, not a failure.
        var photos = MapPhotos(payload.Photos, maxPhotos, formattedDate, rover);

        _logger.LogInformation(
            "Fetched {PhotoCount} NASA photos for {EarthDate} on rover {Rover}",
            photos.Count,
            formattedDate,
            rover);

        return photos;
    }

    private Uri BuildRequestUri(DateOnly earthDate, string rover)
    {
        // BaseAddress already ends with a slash, so a relative URI resolves against it rather than
        // replacing its path. Every interpolated value is escaped: rover names and keys are data.
        var baseAddress = new Uri(_options.BaseAddress, UriKind.Absolute);

        var relative =
            $"rovers/{Uri.EscapeDataString(rover)}/photos" +
            $"?earth_date={Uri.EscapeDataString(earthDate.ToString(EarthDateFormat, CultureInfo.InvariantCulture))}" +
            $"&api_key={Uri.EscapeDataString(_options.ApiKey)}";

        return new Uri(baseAddress, relative);
    }

    private List<MarsPhoto> MapPhotos(
        List<NasaPhotoDto>? dtos,
        int maxPhotos,
        string formattedDate,
        string rover)
    {
        if (dtos is null || dtos.Count == 0)
        {
            return new List<MarsPhoto>();
        }

        var mapped = new List<MarsPhoto>(Math.Min(dtos.Count, maxPhotos));

        // Truncation happens on the way in so API order is preserved and we never map more than needed.
        foreach (var dto in dtos)
        {
            if (mapped.Count == maxPhotos)
            {
                break;
            }

            mapped.Add(MapPhoto(dto, formattedDate, rover));
        }

        return mapped;
    }

    private MarsPhoto MapPhoto(NasaPhotoDto dto, string formattedDate, string rover)
    {
        if (string.IsNullOrWhiteSpace(dto.ImgSrc) ||
            !Uri.TryCreate(dto.ImgSrc, UriKind.Absolute, out var imageUri))
        {
            throw Fail(
                $"NASA photo {dto.Id} for {formattedDate} has a missing or unusable img_src.",
                formattedDate,
                rover);
        }

        if (!DateOnly.TryParseExact(
                dto.EarthDate,
                EarthDateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsedDate))
        {
            throw Fail(
                $"NASA photo {dto.Id} has an earth_date of '{dto.EarthDate}' which is not {EarthDateFormat}.",
                formattedDate,
                rover);
        }

        return new MarsPhoto(
            dto.Id,
            ForceHttps(imageUri),
            parsedDate,
            dto.Rover?.Name ?? rover,
            dto.Camera?.Name ?? "unknown");
    }

    /// <summary>
    /// NASA still hands back plain http img_src values for older photos. We upgrade them because the
    /// host serves the same bytes over TLS, and an http download would otherwise be unauthenticated
    /// and blocked as mixed content by anything that later embeds the URL.
    /// </summary>
    private static Uri ForceHttps(Uri imageUri)
    {
        if (!string.Equals(imageUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return imageUri;
        }

        var builder = new UriBuilder(imageUri)
        {
            Scheme = Uri.UriSchemeHttps,
            // -1 drops the explicit port so the https default is used instead of the http one.
            Port = imageUri.IsDefaultPort ? -1 : imageUri.Port
        };

        return builder.Uri;
    }

    private static string DescribeStatus(HttpStatusCode statusCode, string formattedDate, string rover) =>
        statusCode switch
        {
            // DEMO_KEY allows only a handful of requests per hour per IP, so 429 is the common failure.
            HttpStatusCode.TooManyRequests =>
                $"NASA rate limited the request for {formattedDate} on rover {rover} (429 Too Many Requests). " +
                "DEMO_KEY has a very low hourly quota: register a free API key or slow the run down.",
            HttpStatusCode.Forbidden =>
                $"NASA rejected the request for {formattedDate} on rover {rover} (403 Forbidden). " +
                "The configured API key is missing or invalid: check the Nasa:ApiKey setting or NASA_API_KEY.",
            _ =>
                $"NASA returned HTTP {(int)statusCode} ({statusCode}) for {formattedDate} on rover {rover}."
        };

    private MarsPhotoClientException Fail(
        string message,
        string formattedDate,
        string rover,
        Exception? innerException = null)
    {
        _logger.LogWarning(
            innerException,
            "Failed to fetch NASA photos for {EarthDate} on rover {Rover}: {Reason}",
            formattedDate,
            rover,
            message);

        return new MarsPhotoClientException(message, innerException);
    }
}
