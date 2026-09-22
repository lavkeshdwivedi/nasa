using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Models;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace MarsRoverPhotos.Core.Storage;

/// <summary>
/// Downloads one image over HTTP into the store. Network and disk failures are reported as
/// PhotoDownloadResult.Failed so one bad photo never aborts the run.
/// </summary>
public sealed class HttpPhotoDownloader : IPhotoDownloader
{
    private readonly HttpClient _httpClient;
    private readonly IPhotoStore _store;
    private readonly ILogger<HttpPhotoDownloader> _logger;

    // HttpClient is injected (IHttpClientFactory owns its lifetime and handler pooling); never constructed here.
    public HttpPhotoDownloader(HttpClient httpClient, IPhotoStore store, ILogger<HttpPhotoDownloader> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<PhotoDownloadResult> DownloadAsync(MarsPhoto photo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);

        // Dedupe gate: "do not re-download images that already exist". The store only reports Exists
        // for a non-empty file, so a truncated leftover falls through and is fetched again.
        if (_store.Exists(photo))
        {
            var existingPath = _store.GetPhotoPath(photo);
            _logger.LogDebug("Skipped photo {PhotoId}, already on disk at {Path}", photo.Id, existingPath);
            return PhotoDownloadResult.Skipped(photo, existingPath);
        }

        try
        {
            // ResponseHeadersRead plus a stream copy: the image is never fully buffered in memory.
            using var response = await _httpClient
                .GetAsync(photo.ImageSourceUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {photo.ImageSourceUrl}";
                _logger.LogWarning("Download of photo {PhotoId} failed: {Error}", photo.Id, error);
                return PhotoDownloadResult.Failed(photo, error);
            }

            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var localPath = await _store.SaveAsync(photo, content, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Downloaded photo {PhotoId} to {Path}", photo.Id, localPath);
            return PhotoDownloadResult.Downloaded(photo, localPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop, so this is not a photo-level failure. It must propagate, otherwise
            // a Ctrl+C or a shutting-down host would look like a run where every photo merely failed.
            // This filter has to come first: HttpClient's own timeout also surfaces as a
            // TaskCanceledException, and that one is a failure, handled just below.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            var error = $"Timed out downloading {photo.ImageSourceUrl}";
            _logger.LogWarning(ex, "Download of photo {PhotoId} timed out", photo.Id);
            return PhotoDownloadResult.Failed(photo, error);
        }
        catch (TimeoutRejectedException ex)
        {
            // The resilience handler's per-attempt timeout throws Polly's own exception type rather
            // than TaskCanceledException, so it needs its own catch or it escapes as an unhandled fault.
            var error = $"Timed out downloading {photo.ImageSourceUrl}";
            _logger.LogWarning(ex, "Download of photo {PhotoId} timed out", photo.Id);
            return PhotoDownloadResult.Failed(photo, error);
        }
        catch (BrokenCircuitException ex)
        {
            // The circuit breaker short-circuits further attempts after repeated failures; that is a
            // failure of this one photo, not a fatal fault that should abort the whole run.
            var error = $"Skipped downloading {photo.ImageSourceUrl}: circuit breaker open after repeated failures";
            _logger.LogWarning(ex, "Download of photo {PhotoId} skipped, circuit open", photo.Id);
            return PhotoDownloadResult.Failed(photo, error);
        }
        catch (HttpRequestException ex)
        {
            var error = $"Network error downloading {photo.ImageSourceUrl}: {ex.Message}";
            _logger.LogWarning(ex, "Download of photo {PhotoId} failed", photo.Id);
            return PhotoDownloadResult.Failed(photo, error);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var error = $"Could not write photo {photo.Id} to disk: {ex.Message}";
            _logger.LogWarning(ex, "Saving photo {PhotoId} failed", photo.Id);
            return PhotoDownloadResult.Failed(photo, error);
        }
    }
}
