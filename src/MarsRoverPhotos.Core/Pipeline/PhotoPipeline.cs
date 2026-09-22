using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Models;
using MarsRoverPhotos.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarsRoverPhotos.Core.Pipeline;

/// <summary>
/// Orchestrates the whole run: parse each line, fetch that date's photos, download them.
/// Every failure mode that is expected in normal operation (a malformed line, a NASA outage,
/// a broken image URL) is turned into data on the report rather than an exception, so one bad
/// date can never abort the run.
/// </summary>
public sealed class PhotoPipeline : IPhotoPipeline
{
    private readonly IDateFileReader _dateFileReader;
    private readonly IDateParser _dateParser;
    private readonly IMarsPhotoClient _photoClient;
    private readonly IPhotoDownloader _photoDownloader;
    private readonly IPhotoStore _photoStore;
    private readonly NasaOptions _nasaOptions;
    private readonly StorageOptions _storageOptions;
    private readonly InputOptions _inputOptions;
    private readonly ILogger<PhotoPipeline> _logger;

    public PhotoPipeline(
        IDateFileReader dateFileReader,
        IDateParser dateParser,
        IMarsPhotoClient photoClient,
        IPhotoDownloader photoDownloader,
        IPhotoStore photoStore,
        IOptions<NasaOptions> nasaOptions,
        IOptions<StorageOptions> storageOptions,
        IOptions<InputOptions> inputOptions,
        ILogger<PhotoPipeline> logger)
    {
        _dateFileReader = dateFileReader;
        _dateParser = dateParser;
        _photoClient = photoClient;
        _photoDownloader = photoDownloader;
        _photoStore = photoStore;
        _nasaOptions = nasaOptions.Value;
        _storageOptions = storageOptions.Value;
        _inputOptions = inputOptions.Value;
        _logger = logger;
    }

    public async Task<RunReport> RunAsync(string? datesFilePath = null, CancellationToken cancellationToken = default)
    {
        var path = string.IsNullOrWhiteSpace(datesFilePath)
            ? _inputOptions.DatesFilePath
            : datesFilePath;

        _logger.LogInformation("Reading dates from {DatesFilePath}", path);

        var lines = await _dateFileReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        return await RunAsync(lines, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RunReport> RunAsync(IReadOnlyList<RawDateLine> lines, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<DateResult>(lines.Count);

        // Dates are processed one after another on purpose. The NASA API rate limits per IP and
        // DEMO_KEY is capped at roughly 30 requests an hour, so firing the dates off in parallel
        // buys very little and gets the whole run throttled. Parallelism is applied inside a
        // single date instead, where the requests go to the image host rather than the API.
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ProcessLineAsync(line, cancellationToken).ConfigureAwait(false));
        }

        var report = new RunReport(_nasaOptions.Rover, startedAt, DateTimeOffset.UtcNow, results);

        _logger.LogInformation(
            "Run finished for rover {Rover}: {ValidDates} valid and {InvalidDates} invalid dates, {Downloaded} downloaded, {Skipped} skipped, {Failed} failed in {ElapsedMs} ms",
            report.Rover,
            report.TotalValidDates,
            report.TotalInvalidDates,
            report.TotalPhotosDownloaded,
            report.TotalPhotosSkipped,
            report.TotalPhotosFailed,
            (long)report.Duration.TotalMilliseconds);

        return report;
    }

    private async Task<DateResult> ProcessLineAsync(RawDateLine line, CancellationToken cancellationToken)
    {
        var outcome = _dateParser.Parse(line);

        if (!outcome.IsValid)
        {
            var error = outcome.Error ?? $"Could not parse '{line.Value}' as a date.";

            // No date means no request: an unparseable line must never cost an API call.
            _logger.LogInformation(
                "Line {LineNumber} '{RawInput}' is not a valid date: {Error}",
                line.LineNumber,
                line.Value,
                error);

            return new DateResult(
                line.LineNumber,
                line.Value,
                EarthDate: null,
                IsValid: false,
                PhotosDownloaded: 0,
                PhotosSkipped: 0,
                PhotosFailed: 0,
                FolderPath: null,
                Errors: new[] { error });
        }

        var earthDate = outcome.Date!.Value;
        var folderPath = DisplayPath(_photoStore.GetFolderPath(earthDate));

        IReadOnlyList<MarsPhoto> photos;
        try
        {
            photos = await _photoClient
                .GetPhotosAsync(earthDate, _nasaOptions.Rover, _nasaOptions.MaxPhotosPerDate, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (MarsPhotoClientException ex)
        {
            // A client that wraps its failures could hide a cancellation in here, and a cancelled
            // run should stop rather than report every remaining date as an API failure.
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogError(
                ex,
                "Line {LineNumber} {EarthDate:yyyy-MM-dd}: NASA request failed, continuing with the remaining dates",
                line.LineNumber,
                earthDate);

            return new DateResult(
                line.LineNumber,
                line.Value,
                earthDate,
                IsValid: true,
                PhotosDownloaded: 0,
                PhotosSkipped: 0,
                PhotosFailed: 0,
                folderPath,
                Errors: new[] { ex.Message });
        }

        var downloads = await DownloadAllAsync(photos, cancellationToken).ConfigureAwait(false);

        var downloaded = 0;
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var download in downloads)
        {
            switch (download.Status)
            {
                case PhotoDownloadStatus.Downloaded:
                    downloaded++;
                    break;
                case PhotoDownloadStatus.Skipped:
                    skipped++;
                    break;
                case PhotoDownloadStatus.Failed:
                    failed++;
                    errors.Add(download.Error ?? $"Photo {download.Photo.Id} failed to download.");
                    break;
            }
        }

        _logger.LogInformation(
            "Line {LineNumber} {EarthDate:yyyy-MM-dd}: {PhotoCount} photos, {Downloaded} downloaded, {Skipped} already on disk, {Failed} failed, folder {FolderPath}",
            line.LineNumber,
            earthDate,
            photos.Count,
            downloaded,
            skipped,
            failed,
            folderPath);

        return new DateResult(
            line.LineNumber,
            line.Value,
            earthDate,
            IsValid: true,
            downloaded,
            skipped,
            failed,
            folderPath,
            errors);
    }

    /// <summary>
    /// Downloads one date's photos with at most <see cref="StorageOptions.MaxParallelDownloads"/>
    /// in flight. Results come back in the order the photos were supplied.
    /// </summary>
    private async Task<IReadOnlyList<PhotoDownloadResult>> DownloadAllAsync(
        IReadOnlyList<MarsPhoto> photos,
        CancellationToken cancellationToken)
    {
        if (photos.Count == 0)
        {
            return Array.Empty<PhotoDownloadResult>();
        }

        var maxParallel = Math.Max(1, _storageOptions.MaxParallelDownloads);
        using var gate = new SemaphoreSlim(maxParallel, maxParallel);

        var tasks = new Task<PhotoDownloadResult>[photos.Count];
        for (var i = 0; i < photos.Count; i++)
        {
            tasks[i] = DownloadOneAsync(photos[i], gate, cancellationToken);
        }

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<PhotoDownloadResult> DownloadOneAsync(
        MarsPhoto photo,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _photoDownloader.DownloadAsync(photo, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Prefers a path relative to the working directory because that is what a person reading the
    /// console output actually wants, but keeps the absolute one when the relative form is longer,
    /// which happens once the store lives outside the working directory.
    /// </summary>
    private static string DisplayPath(string folderPath)
    {
        var absolute = Path.GetFullPath(folderPath);
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), absolute);
        return relative.Length < absolute.Length ? relative : absolute;
    }
}
