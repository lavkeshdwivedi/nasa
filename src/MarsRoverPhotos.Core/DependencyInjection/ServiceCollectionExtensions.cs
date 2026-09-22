using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Dates;
using MarsRoverPhotos.Core.Nasa;
using MarsRoverPhotos.Core.Options;
using MarsRoverPhotos.Core.Pipeline;
using MarsRoverPhotos.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace MarsRoverPhotos.Core.DependencyInjection;

/// <summary>
/// The single composition root for the library. The CLI and the API both call
/// <see cref="AddMarsRoverPhotos"/> so they cannot drift apart in how they are wired.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Overrides the bound API key so a key never has to be written into appsettings.json.</summary>
    public const string ApiKeyEnvironmentVariable = "NASA_API_KEY";

    private const string UserAgent = "MarsRoverPhotos/1.0 (+dotnet-9)";

    public static IServiceCollection AddMarsRoverPhotos(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<NasaOptions>()
            .Bind(configuration.GetSection(NasaOptions.SectionName))
            .PostConfigure(ApplyApiKeyOverride)
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ApiKey),
                $"The NASA API key is empty. Set Nasa:ApiKey in configuration or user secrets, or set the {ApiKeyEnvironmentVariable} environment variable. Leave it unset to fall back to DEMO_KEY.")
            .Validate(
                options => Uri.TryCreate(options.BaseAddress, UriKind.Absolute, out _),
                "Nasa:BaseAddress must be an absolute URL, for example https://api.nasa.gov/mars-photos/api/v1/.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.Rover),
                "Nasa:Rover must name a rover, for example curiosity.")
            .ValidateOnStart();

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(
                options => options.MaxParallelDownloads >= 1,
                "Storage:MaxParallelDownloads must be at least 1.")
            .ValidateOnStart();

        services.AddOptions<InputOptions>()
            .Bind(configuration.GetSection(InputOptions.SectionName))
            .ValidateOnStart();

        // The HttpClient and the resilience pipeline are built once at startup, so they read a
        // snapshot of the configuration rather than IOptionsMonitor. The same override is applied
        // here as in PostConfigure so the snapshot cannot disagree with the injected options.
        var nasaSnapshot = configuration.GetSection(NasaOptions.SectionName).Get<NasaOptions>() ?? new NasaOptions();
        ApplyApiKeyOverride(nasaSnapshot);

        // DEMO_KEY is the documented fallback and it works, but NASA throttles it to roughly
        // 30 requests an hour per IP (and 50 a day), so any real use wants a personal key.
        services.AddHttpClient<IMarsPhotoClient, NasaMarsPhotoClient>(client =>
            ConfigureClient(client, nasaSnapshot))
            .AddStandardResilienceHandler(options => ConfigureResilience(options, nasaSnapshot));

        services.AddHttpClient<IPhotoDownloader, HttpPhotoDownloader>(client =>
            ConfigureClient(client, nasaSnapshot))
            .AddStandardResilienceHandler(options => ConfigureResilience(options, nasaSnapshot));

        // Stateless and cheap to share, so a singleton each.
        services.AddSingleton<IDateFileReader, DateFileReader>();
        // The parser takes an optional TimeProvider so a test can pin "today". Resolved with
        // GetService rather than the two type argument overload so the registration still works
        // when the host has not registered a TimeProvider of its own.
        services.AddSingleton<IDateParser>(provider =>
            new MultiFormatDateParser(provider.GetService<TimeProvider>()));
        services.AddSingleton<IPhotoStore, FileSystemPhotoStore>();

        // One pipeline per run: it is short lived and holds the state of that run only.
        services.AddTransient<IPhotoPipeline, PhotoPipeline>();

        return services;
    }

    /// <summary>
    /// Lets <c>NASA_API_KEY</c> win over anything bound from configuration. An unset variable
    /// leaves the bound value alone, which keeps the DEMO_KEY default on <see cref="NasaOptions"/>
    /// as the fallback; an explicitly blank key is left blank so validation can reject it.
    /// </summary>
    private static void ApplyApiKeyOverride(NasaOptions options)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            options.ApiKey = fromEnvironment.Trim();
        }
    }

    private static void ConfigureClient(HttpClient client, NasaOptions nasa)
    {
        if (Uri.TryCreate(nasa.BaseAddress, UriKind.Absolute, out var baseAddress))
        {
            client.BaseAddress = baseAddress;
        }

        // HttpClient.Timeout would cover the whole pipeline, retries included, so it is set to the
        // budget for every attempt rather than for one. The standard resilience handler then
        // relaxes it to an infinite timeout and takes the budget over itself, which is why the
        // real limits are AttemptTimeout and TotalRequestTimeout, both derived from TimeoutSeconds.
        // The value below only matters if the resilience handler is ever taken back out.
        client.Timeout = TotalBudget(nasa);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    private static void ConfigureResilience(HttpStandardResilienceOptions options, NasaOptions nasa)
    {
        var attemptTimeout = AttemptTimeout(nasa);
        var retries = RetryAttempts(nasa);

        options.AttemptTimeout.Timeout = attemptTimeout;
        options.TotalRequestTimeout.Timeout = TotalBudget(nasa);

        // The standard handler validates that the sampling window is at least double the attempt
        // timeout, so it has to move with it rather than stay at its default.
        options.CircuitBreaker.SamplingDuration = attemptTimeout * 2;

        options.Retry.MaxRetryAttempts = retries;
        options.Retry.Delay = TimeSpan.FromSeconds(1);
        options.Retry.BackoffType = DelayBackoffType.Exponential;

        // Jitter matters more than usual here: several photos for one date retry at once and a
        // fixed backoff would send them all back at the same instant.
        options.Retry.UseJitter = true;
    }

    private static TimeSpan AttemptTimeout(NasaOptions nasa) =>
        TimeSpan.FromSeconds(Math.Max(1, nasa.TimeoutSeconds));

    private static int RetryAttempts(NasaOptions nasa) =>
        Math.Clamp(nasa.MaxRetries, 1, 10);

    /// <summary>Room for the first attempt plus every retry, with a little slack for the backoff waits.</summary>
    private static TimeSpan TotalBudget(NasaOptions nasa) =>
        AttemptTimeout(nasa) * (RetryAttempts(nasa) + 2);
}
