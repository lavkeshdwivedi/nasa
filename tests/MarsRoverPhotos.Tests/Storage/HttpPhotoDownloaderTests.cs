using System.Net;
using System.Text;
using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Models;
using MarsRoverPhotos.Core.Options;
using MarsRoverPhotos.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MarsRoverPhotos.Tests.Storage;

public sealed class HttpPhotoDownloaderTests : IDisposable
{
    private static readonly byte[] ImageBytes = Encoding.UTF8.GetBytes("pretend this is a jpeg");

    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [Fact]
    public async Task DownloadAsync_WritesTheResponseBytesToTheDatedFolder()
    {
        var store = CreateStore();
        var handler = new FakeHttpMessageHandler(_ => Ok(ImageBytes));
        var downloader = CreateDownloader(handler, store);
        var photo = CreatePhoto();

        var result = await downloader.DownloadAsync(photo, CancellationToken.None);

        Assert.Equal(PhotoDownloadStatus.Downloaded, result.Status);
        Assert.Equal(store.GetPhotoPath(photo), result.LocalPath);
        Assert.Null(result.Error);
        Assert.Equal(ImageBytes, await File.ReadAllBytesAsync(result.LocalPath!));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_SkipsASecondDownloadOfTheSamePhotoWithoutHittingTheNetwork()
    {
        var store = CreateStore();
        var handler = new FakeHttpMessageHandler(_ => Ok(ImageBytes));
        var downloader = CreateDownloader(handler, store);
        var photo = CreatePhoto();

        var first = await downloader.DownloadAsync(photo, CancellationToken.None);
        var second = await downloader.DownloadAsync(photo, CancellationToken.None);

        Assert.Equal(PhotoDownloadStatus.Downloaded, first.Status);
        Assert.Equal(PhotoDownloadStatus.Skipped, second.Status);
        Assert.Equal(first.LocalPath, second.LocalPath);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task DownloadAsync_RedownloadsWhenTheCachedFileIsZeroBytes()
    {
        var store = CreateStore();
        var handler = new FakeHttpMessageHandler(_ => Ok(ImageBytes));
        var downloader = CreateDownloader(handler, store);
        var photo = CreatePhoto();
        var path = store.GetPhotoPath(photo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, []);

        var result = await downloader.DownloadAsync(photo, CancellationToken.None);

        Assert.Equal(PhotoDownloadStatus.Downloaded, result.Status);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(ImageBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DownloadAsync_ReturnsFailedWithTheStatusCode_WhenTheImageIsMissing()
    {
        var store = Substitute.For<IPhotoStore>();
        store.Exists(Arg.Any<MarsPhoto>()).Returns(false);
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var downloader = CreateDownloader(handler, store);

        var result = await downloader.DownloadAsync(CreatePhoto(), CancellationToken.None);

        Assert.Equal(PhotoDownloadStatus.Failed, result.Status);
        Assert.Null(result.LocalPath);
        Assert.Contains("404", result.Error);
        await store.DidNotReceive().SaveAsync(Arg.Any<MarsPhoto>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadAsync_ReturnsFailed_WhenTheRequestThrowsANetworkError()
    {
        var store = Substitute.For<IPhotoStore>();
        store.Exists(Arg.Any<MarsPhoto>()).Returns(false);
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("name resolution failed"));
        var downloader = CreateDownloader(handler, store);

        var result = await downloader.DownloadAsync(CreatePhoto(), CancellationToken.None);

        Assert.Equal(PhotoDownloadStatus.Failed, result.Status);
        Assert.Contains("name resolution failed", result.Error);
        await store.DidNotReceive().SaveAsync(Arg.Any<MarsPhoto>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadAsync_ReturnsFailed_WhenTheRequestTimesOut()
    {
        var store = Substitute.For<IPhotoStore>();
        store.Exists(Arg.Any<MarsPhoto>()).Returns(false);

        // A timeout reaches us as a TaskCanceledException while the caller token is still live,
        // which is the case that must be reported as Failed rather than rethrown.
        var handler = new FakeHttpMessageHandler(_ => throw new TaskCanceledException("timed out"));
        var downloader = CreateDownloader(handler, store);

        var result = await downloader.DownloadAsync(CreatePhoto(), CancellationToken.None);

        Assert.Equal(PhotoDownloadStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task DownloadAsync_LetsCallerCancellationPropagate()
    {
        var store = CreateStore();
        var handler = new FakeHttpMessageHandler(_ => Ok(ImageBytes));
        var downloader = CreateDownloader(handler, store);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => downloader.DownloadAsync(CreatePhoto(), cts.Token));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A locked temp file must not fail the test run.
        }
    }

    private FileSystemPhotoStore CreateStore() =>
        new(Options.Create(new StorageOptions { RootPath = _root }), NullLogger<FileSystemPhotoStore>.Instance);

    private static HttpPhotoDownloader CreateDownloader(FakeHttpMessageHandler handler, IPhotoStore store) =>
        new(new HttpClient(handler), store, NullLogger<HttpPhotoDownloader>.Instance);

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static MarsPhoto CreatePhoto() =>
        new(
            424905,
            new Uri("https://mars.nasa.gov/msl-raw-images/FRB_486265257EDR_F0481570FHAZ00323M_.JPG"),
            new DateOnly(2017, 2, 27),
            "curiosity",
            "FHAZ");

    /// <summary>Returns canned responses and counts how many requests actually reached the network.</summary>
    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
