using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Nasa;
using MarsRoverPhotos.Core.Options;

namespace MarsRoverPhotos.Tests.Nasa;

public sealed class NasaMarsPhotoClientTests
{
    private static readonly DateOnly EarthDate = new(2017, 2, 27);

    private const string TwoPhotoPayload = """
        {
          "photos": [
            {
              "id": 123,
              "img_src": "https://mars.nasa.gov/first.jpg",
              "earth_date": "2017-02-27",
              "camera": { "name": "FHAZ", "full_name": "Front Hazard Avoidance Camera" },
              "rover": { "name": "Curiosity" }
            },
            {
              "id": 124,
              "img_src": "https://mars.nasa.gov/second.jpg",
              "earth_date": "2017-02-27",
              "camera": { "name": "NAVCAM", "full_name": "Navigation Camera" },
              "rover": { "name": "Curiosity" }
            }
          ]
        }
        """;

    [Fact]
    public async Task GetPhotosAsync_MapsIdImageDateRoverAndCamera()
    {
        var (client, _) = CreateClient(Json(TwoPhotoPayload));

        var photos = await client.GetPhotosAsync(EarthDate, "curiosity", 10);

        Assert.Equal(2, photos.Count);

        var first = photos[0];
        Assert.Equal(123, first.Id);
        Assert.Equal(new Uri("https://mars.nasa.gov/first.jpg"), first.ImageSourceUrl);
        Assert.Equal(EarthDate, first.EarthDate);
        Assert.Equal("Curiosity", first.RoverName);
        Assert.Equal("FHAZ", first.CameraName);

        Assert.Equal(124, photos[1].Id);
        Assert.Equal("NAVCAM", photos[1].CameraName);
    }

    [Fact]
    public async Task GetPhotosAsync_TruncatesToMaxPhotosKeepingApiOrder()
    {
        var (client, _) = CreateClient(Json(TwoPhotoPayload));

        var photos = await client.GetPhotosAsync(EarthDate, "curiosity", 1);

        Assert.Single(photos);
        Assert.Equal(123, photos[0].Id);
    }

    [Fact]
    public async Task GetPhotosAsync_ReturnsEmptyListWhenNasaHasNoPhotosForTheDate()
    {
        var (client, _) = CreateClient(Json("""{ "photos": [] }"""));

        var photos = await client.GetPhotosAsync(EarthDate, "curiosity", 10);

        Assert.Empty(photos);
    }

    [Fact]
    public async Task GetPhotosAsync_RewritesHttpImageUrlsToHttps()
    {
        var payload = """
            {
              "photos": [
                {
                  "id": 1,
                  "img_src": "http://mars.jpl.nasa.gov/legacy.jpg",
                  "earth_date": "2017-02-27",
                  "camera": { "name": "MAST", "full_name": "Mast Camera" },
                  "rover": { "name": "Curiosity" }
                }
              ]
            }
            """;

        var (client, _) = CreateClient(Json(payload));

        var photos = await client.GetPhotosAsync(EarthDate, "curiosity", 10);

        Assert.Equal("https", photos[0].ImageSourceUrl.Scheme);
        Assert.Equal("https://mars.jpl.nasa.gov/legacy.jpg", photos[0].ImageSourceUrl.ToString());
    }

    [Fact]
    public async Task GetPhotosAsync_SendsFormattedEarthDateAndConfiguredApiKey()
    {
        var (client, handler) = CreateClient(Json(TwoPhotoPayload), apiKey: "test-key-123");

        await client.GetPhotosAsync(EarthDate, "curiosity", 10);

        Assert.NotNull(handler.LastRequestUri);
        var url = handler.LastRequestUri!.ToString();

        Assert.Contains("rovers/curiosity/photos", url);
        Assert.Contains("earth_date=2017-02-27", url);
        Assert.Contains("api_key=test-key-123", url);
        Assert.StartsWith("https://api.nasa.gov/mars-photos/api/v1/rovers/", url);
    }

    [Fact]
    public async Task GetPhotosAsync_ThrowsWithApiKeyHintOnForbidden()
    {
        var (client, _) = CreateClient(new HttpResponseMessage(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<MarsPhotoClientException>(
            () => client.GetPhotosAsync(EarthDate, "curiosity", 10));

        Assert.Contains("403", ex.Message);
        Assert.Contains("API key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetPhotosAsync_ThrowsWithRateLimitHintOnTooManyRequests()
    {
        var (client, _) = CreateClient(new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var ex = await Assert.ThrowsAsync<MarsPhotoClientException>(
            () => client.GetPhotosAsync(EarthDate, "curiosity", 10));

        Assert.Contains("429", ex.Message);
        Assert.Contains("rate limited", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DEMO_KEY", ex.Message);
    }

    [Fact]
    public async Task GetPhotosAsync_ThrowsOnServerError()
    {
        var (client, _) = CreateClient(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<MarsPhotoClientException>(
            () => client.GetPhotosAsync(EarthDate, "curiosity", 10));

        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task GetPhotosAsync_ThrowsOnMalformedJson()
    {
        var (client, _) = CreateClient(Json("{ \"photos\": [ this is not json"));

        var ex = await Assert.ThrowsAsync<MarsPhotoClientException>(
            () => client.GetPhotosAsync(EarthDate, "curiosity", 10));

        Assert.Contains("JSON", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task GetPhotosAsync_WrapsNetworkFailuresAndNeverLeaksHttpRequestException()
    {
        var (client, _) = CreateClient(new HttpRequestException("name resolution failed"));

        var ex = await Assert.ThrowsAsync<MarsPhotoClientException>(
            () => client.GetPhotosAsync(EarthDate, "curiosity", 10));

        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task GetPhotosAsync_WrapsHttpClientTimeoutAsClientException()
    {
        // HttpClient signals its own timeout as TaskCanceledException with nothing cancelled by us.
        var (client, _) = CreateClient(new TaskCanceledException("timed out"));

        var ex = await Assert.ThrowsAsync<MarsPhotoClientException>(
            () => client.GetPhotosAsync(EarthDate, "curiosity", 10));

        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task GetPhotosAsync_PropagatesCallerCancellation()
    {
        var (client, _) = CreateClient(Json(TwoPhotoPayload));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetPhotosAsync(EarthDate, "curiosity", 10, cts.Token));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static (NasaMarsPhotoClient Client, FakeHttpMessageHandler Handler) CreateClient(
        HttpResponseMessage response,
        string apiKey = "DEMO_KEY") =>
        CreateClient(new FakeHttpMessageHandler(response), apiKey);

    private static (NasaMarsPhotoClient Client, FakeHttpMessageHandler Handler) CreateClient(
        Exception thrown,
        string apiKey = "DEMO_KEY") =>
        CreateClient(new FakeHttpMessageHandler(thrown), apiKey);

    private static (NasaMarsPhotoClient Client, FakeHttpMessageHandler Handler) CreateClient(
        FakeHttpMessageHandler handler,
        string apiKey)
    {
        var httpClient = new HttpClient(handler);
        var options = Microsoft.Extensions.Options.Options.Create(new NasaOptions
        {
            ApiKey = apiKey,
            BaseAddress = "https://api.nasa.gov/mars-photos/api/v1/"
        });

        var client = new NasaMarsPhotoClient(
            httpClient,
            options,
            NullLogger<NasaMarsPhotoClient>.Instance);

        return (client, handler);
    }

    /// <summary>Serves one canned response (or throws one canned exception) and records the request URI.</summary>
    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly Exception? _exception;

        public FakeHttpMessageHandler(HttpResponseMessage response) => _response = response;

        public FakeHttpMessageHandler(Exception exception) => _exception = exception;

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            cancellationToken.ThrowIfCancellationRequested();

            if (_exception is not null)
            {
                return Task.FromException<HttpResponseMessage>(_exception);
            }

            return Task.FromResult(_response!);
        }
    }
}
