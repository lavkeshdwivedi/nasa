using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Models;
using MarsRoverPhotos.Core.Options;
using MarsRoverPhotos.Core.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MarsRoverPhotos.Tests.Pipeline;

public sealed class PhotoPipelineTests
{
    private static readonly DateOnly ValidDate = new(2020, 1, 1);
    private static readonly DateOnly OtherValidDate = new(2020, 1, 2);

    private readonly IDateFileReader _fileReader = Substitute.For<IDateFileReader>();
    private readonly IDateParser _parser = Substitute.For<IDateParser>();
    private readonly IMarsPhotoClient _client = Substitute.For<IMarsPhotoClient>();
    private readonly IPhotoDownloader _downloader = Substitute.For<IPhotoDownloader>();
    private readonly IPhotoStore _store = Substitute.For<IPhotoStore>();

    private readonly NasaOptions _nasa = new() { Rover = "curiosity", MaxPhotosPerDate = 5 };
    private readonly StorageOptions _storage = new() { RootPath = "photos", MaxParallelDownloads = 4 };
    private readonly InputOptions _input = new() { DatesFilePath = "dates.txt" };

    public PhotoPipelineTests()
    {
        _store.GetFolderPath(Arg.Any<DateOnly>())
            .Returns(call => Path.Combine("photos", call.Arg<DateOnly>().ToString("yyyy-MM-dd")));

        // Default: nothing to download for any date unless a test says otherwise.
        _client.GetPhotosAsync(Arg.Any<DateOnly>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<MarsPhoto>());
    }

    [Fact]
    public async Task RunAsync_WithMixedInput_ReportsEachLineAndNeverCallsTheApiForAnInvalidDate()
    {
        var lines = new[]
        {
            new RawDateLine(1, "2020-01-01"),
            new RawDateLine(2, "not a date"),
            new RawDateLine(3, "02/01/2020")
        };

        ParseAs(lines[0], ValidDate);
        ParseFails(lines[1], "Unrecognised date format.");
        ParseAs(lines[2], OtherValidDate);

        SetPhotos(ValidDate, Photo(1, ValidDate));
        SetDownload(1, PhotoDownloadResult.Downloaded(Photo(1, ValidDate), "photos/2020-01-01/1_FHAZ.jpg"));

        var report = await CreatePipeline().RunAsync(lines);

        Assert.Equal(3, report.Results.Count);
        Assert.Equal(new[] { 1, 2, 3 }, report.Results.Select(r => r.LineNumber));

        var first = report.Results[0];
        Assert.True(first.IsValid);
        Assert.Equal(ValidDate, first.EarthDate);
        Assert.Equal(1, first.PhotosDownloaded);
        Assert.Empty(first.Errors);
        Assert.NotNull(first.FolderPath);
        Assert.Contains("2020-01-01", first.FolderPath);

        var invalid = report.Results[1];
        Assert.False(invalid.IsValid);
        Assert.Null(invalid.EarthDate);
        Assert.Null(invalid.FolderPath);
        Assert.Equal(0, invalid.PhotosDownloaded);
        Assert.Equal(0, invalid.PhotosSkipped);
        Assert.Equal(0, invalid.PhotosFailed);
        Assert.Equal("Unrecognised date format.", Assert.Single(invalid.Errors));

        var third = report.Results[2];
        Assert.True(third.IsValid);
        Assert.Equal(OtherValidDate, third.EarthDate);

        // The API is asked about the two valid dates and nothing else.
        await _client.Received(1).GetPhotosAsync(ValidDate, "curiosity", 5, Arg.Any<CancellationToken>());
        await _client.Received(1).GetPhotosAsync(OtherValidDate, "curiosity", 5, Arg.Any<CancellationToken>());
        Assert.Equal(2, _client.ReceivedCalls().Count());
    }

    [Fact]
    public async Task RunAsync_WhenTheApiFailsForOneDate_StillProcessesTheRest()
    {
        var lines = new[]
        {
            new RawDateLine(1, "2020-01-01"),
            new RawDateLine(2, "2020-01-02")
        };

        ParseAs(lines[0], ValidDate);
        ParseAs(lines[1], OtherValidDate);

        _client.GetPhotosAsync(ValidDate, Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<MarsPhoto>>(_ => throw new MarsPhotoClientException("NASA returned 503."));

        SetPhotos(OtherValidDate, Photo(2, OtherValidDate));
        SetDownload(2, PhotoDownloadResult.Downloaded(Photo(2, OtherValidDate), "photos/2020-01-02/2_FHAZ.jpg"));

        var report = await CreatePipeline().RunAsync(lines);

        var failedDate = report.Results[0];
        Assert.True(failedDate.IsValid);
        Assert.Equal(ValidDate, failedDate.EarthDate);
        Assert.Equal(0, failedDate.PhotosDownloaded);
        Assert.Equal(0, failedDate.PhotosSkipped);
        Assert.Equal(0, failedDate.PhotosFailed);
        Assert.Equal("NASA returned 503.", Assert.Single(failedDate.Errors));
        Assert.NotNull(failedDate.FolderPath);

        var laterDate = report.Results[1];
        Assert.Equal(1, laterDate.PhotosDownloaded);
        Assert.Empty(laterDate.Errors);

        // A failing date is still a valid date, it just has nothing to show for it.
        Assert.Equal(2, report.TotalValidDates);
        Assert.Equal(1, report.TotalPhotosDownloaded);
    }

    [Fact]
    public async Task RunAsync_TalliesDownloadedSkippedAndFailedAndCollectsTheFailureMessages()
    {
        var line = new RawDateLine(1, "2020-01-01");
        ParseAs(line, ValidDate);

        var downloadedPhoto = Photo(1, ValidDate);
        var skippedPhoto = Photo(2, ValidDate);
        var failedPhoto = Photo(3, ValidDate);
        SetPhotos(ValidDate, downloadedPhoto, skippedPhoto, failedPhoto);

        SetDownload(1, PhotoDownloadResult.Downloaded(downloadedPhoto, "photos/2020-01-01/1_FHAZ.jpg"));
        SetDownload(2, PhotoDownloadResult.Skipped(skippedPhoto, "photos/2020-01-01/2_FHAZ.jpg"));
        SetDownload(3, PhotoDownloadResult.Failed(failedPhoto, "Image host returned 404."));

        var report = await CreatePipeline().RunAsync(new[] { line });

        var result = Assert.Single(report.Results);
        Assert.Equal(1, result.PhotosDownloaded);
        Assert.Equal(1, result.PhotosSkipped);
        Assert.Equal(1, result.PhotosFailed);
        Assert.Equal(3, result.PhotosAvailable);
        Assert.Equal("Image host returned 404.", Assert.Single(result.Errors));
    }

    [Fact]
    public async Task RunAsync_ReportTotalsMatchThePerDateResults()
    {
        var lines = new[]
        {
            new RawDateLine(1, "2020-01-01"),
            new RawDateLine(2, "yesterday"),
            new RawDateLine(3, "2020-01-02")
        };

        ParseAs(lines[0], ValidDate);
        ParseFails(lines[1], "Unrecognised date format.");
        ParseAs(lines[2], OtherValidDate);

        var first = Photo(1, ValidDate);
        var second = Photo(2, ValidDate);
        SetPhotos(ValidDate, first, second);
        SetDownload(1, PhotoDownloadResult.Downloaded(first, "photos/2020-01-01/1_FHAZ.jpg"));
        SetDownload(2, PhotoDownloadResult.Skipped(second, "photos/2020-01-01/2_FHAZ.jpg"));

        var third = Photo(3, OtherValidDate);
        SetPhotos(OtherValidDate, third);
        SetDownload(3, PhotoDownloadResult.Failed(third, "Timed out."));

        var before = DateTimeOffset.UtcNow;
        var report = await CreatePipeline().RunAsync(lines);
        var after = DateTimeOffset.UtcNow;

        Assert.Equal("curiosity", report.Rover);
        Assert.Equal(3, report.TotalDatesProcessed);
        Assert.Equal(2, report.TotalValidDates);
        Assert.Equal(1, report.TotalInvalidDates);
        Assert.Equal(1, report.TotalPhotosDownloaded);
        Assert.Equal(1, report.TotalPhotosSkipped);
        Assert.Equal(1, report.TotalPhotosFailed);

        Assert.Equal(report.Results.Sum(r => r.PhotosDownloaded), report.TotalPhotosDownloaded);
        Assert.Equal(report.Results.Sum(r => r.PhotosSkipped), report.TotalPhotosSkipped);
        Assert.Equal(report.Results.Sum(r => r.PhotosFailed), report.TotalPhotosFailed);

        Assert.InRange(report.StartedAt, before, after);
        Assert.InRange(report.CompletedAt, report.StartedAt, after);
        Assert.True(report.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task RunAsync_WithoutAPath_FallsBackToTheConfiguredDatesFile()
    {
        var lines = new[] { new RawDateLine(1, "2020-01-01") };
        ParseAs(lines[0], ValidDate);

        _fileReader.ReadAsync("dates.txt", Arg.Any<CancellationToken>()).Returns(lines);

        var report = await CreatePipeline().RunAsync("   ");

        Assert.Single(report.Results);
        await _fileReader.Received(1).ReadAsync("dates.txt", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WithAnExplicitPath_PrefersItOverTheConfiguredOne()
    {
        var lines = new[] { new RawDateLine(1, "2020-01-01") };
        ParseAs(lines[0], ValidDate);

        _fileReader.ReadAsync("other.txt", Arg.Any<CancellationToken>()).Returns(lines);

        await CreatePipeline().RunAsync("other.txt");

        await _fileReader.Received(1).ReadAsync("other.txt", Arg.Any<CancellationToken>());
        await _fileReader.DidNotReceive().ReadAsync("dates.txt", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_NeverExceedsTheConfiguredDownloadParallelism()
    {
        _storage.MaxParallelDownloads = 2;

        var line = new RawDateLine(1, "2020-01-01");
        ParseAs(line, ValidDate);

        var photos = Enumerable.Range(1, 8).Select(id => Photo(id, ValidDate)).ToArray();
        SetPhotos(ValidDate, photos);

        var inFlight = 0;
        var peak = 0;
        _downloader.DownloadAsync(Arg.Any<MarsPhoto>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var current = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref peak, current);
                try
                {
                    await Task.Delay(20, CancellationToken.None);
                    return PhotoDownloadResult.Downloaded(call.Arg<MarsPhoto>(), "somewhere.jpg");
                }
                finally
                {
                    Interlocked.Decrement(ref inFlight);
                }
            });

        var report = await CreatePipeline().RunAsync(new[] { line });

        Assert.Equal(8, report.TotalPhotosDownloaded);
        Assert.InRange(Volatile.Read(ref peak), 1, 2);
    }

    private PhotoPipeline CreatePipeline() => new(
        _fileReader,
        _parser,
        _client,
        _downloader,
        _store,
        Options.Create(_nasa),
        Options.Create(_storage),
        Options.Create(_input),
        NullLogger<PhotoPipeline>.Instance);

    private void ParseAs(RawDateLine line, DateOnly date) =>
        _parser.Parse(line).Returns(DateParseOutcome.Success(line.LineNumber, line.Value, date));

    private void ParseFails(RawDateLine line, string error) =>
        _parser.Parse(line).Returns(DateParseOutcome.Failure(line.LineNumber, line.Value, error));

    private void SetPhotos(DateOnly date, params MarsPhoto[] photos) =>
        _client.GetPhotosAsync(date, Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(photos);

    private void SetDownload(long photoId, PhotoDownloadResult result) =>
        _downloader.DownloadAsync(Arg.Is<MarsPhoto>(p => p.Id == photoId), Arg.Any<CancellationToken>())
            .Returns(result);

    private static MarsPhoto Photo(int id, DateOnly date) =>
        new(id, new Uri($"https://mars.nasa.gov/photo/{id}.jpg"), date, "Curiosity", "FHAZ");

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
