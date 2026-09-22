using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.DependencyInjection;
using MarsRoverPhotos.Core.Models;
using MarsRoverPhotos.Core.Options;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Every concrete service, option binding and HttpClient lives in Core so the CLI and the API agree.
// That includes the API key: appsettings.json carries only the documented DEMO_KEY fallback, and
// ServiceCollectionExtensions.ApiKeyEnvironmentVariable (NASA_API_KEY) overrides it when set, so
// the key is never read or copied here.
builder.Services.AddMarsRoverPhotos(builder.Configuration);

// Unhandled exceptions become RFC 7807 payloads instead of a bare 500 with no body.
builder.Services.AddProblemDetails();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    // DateOnly already round-trips as yyyy-MM-dd, the explicit converter pins that down so a future
    // serializer default cannot quietly change the wire format the UI parses.
    options.SerializerOptions.Converters.Add(new IsoDateOnlyConverter());
});

var app = builder.Build();

app.UseExceptionHandler();

// index.html ships with the project, so "/" serves the bonus UI.
app.UseDefaultFiles();
app.UseStaticFiles();

// The downloaded photos live outside wwwroot, so they get their own file provider mounted at
// /photos. This is deliberately read-only (the static file middleware answers GET and HEAD only,
// never writes) and scoped to StorageOptions.RootPath, so nothing above that folder is reachable.
var storage = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
var photosRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, storage.RootPath));
// PhysicalFileProvider throws when the directory does not exist yet, which it will not before the
// first run, so the folder is created up front rather than crashing startup on a clean checkout.
Directory.CreateDirectory(photosRoot);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(photosRoot),
    RequestPath = "/photos",
    ServeUnknownFileTypes = false,
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/api/run", async (
    string? rover,
    int? maxPhotos,
    string? datesFile,
    IServiceProvider requestServices,
    IConfiguration configuration,
    IHostEnvironment environment,
    IOptions<InputOptions> input,
    CancellationToken cancellationToken) =>
{
    if (maxPhotos is <= 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["maxPhotos"] = ["maxPhotos must be greater than zero."],
        });
    }

    string? datesFilePath;
    if (!string.IsNullOrWhiteSpace(datesFile))
    {
        // The path arrives from the query string, so it must not escape the content root.
        if (!TryResolveInsideContentRoot(environment.ContentRootPath, datesFile, out var resolved))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["datesFile"] = ["datesFile must be a path inside the application content root."],
            });
        }

        datesFilePath = resolved;
    }
    else
    {
        datesFilePath = ProbeConfiguredDatesFile(environment.ContentRootPath, input.Value.DatesFilePath);
    }

    try
    {
        var report = await ExecuteAsync(
            requestServices,
            configuration,
            rover,
            maxPhotos,
            (pipeline, token) => pipeline.RunAsync(datesFilePath, token),
            cancellationToken);

        return Results.Ok(report);
    }
    catch (FileNotFoundException exception)
    {
        // A missing dates file is a caller or configuration mistake, not a server fault, so it
        // gets a 404 rather than the 500 the exception handler would otherwise produce.
        return Results.Problem(
            title: "Dates file not found",
            detail: exception.Message,
            statusCode: StatusCodes.Status404NotFound);
    }
});

app.MapPost("/api/run", async (
    RunRequest? request,
    IServiceProvider requestServices,
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    // Blank entries are dropped first so the line numbers match what the caller can actually see
    // reported back, the same way the file reader skips empty lines.
    var values = request?.Dates?
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .ToList() ?? [];

    if (values.Count == 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["dates"] = ["Provide at least one non-empty date string."],
        });
    }

    var lines = values
        .Select((value, index) => new RawDateLine(index + 1, value))
        .ToList();

    var report = await ExecuteAsync(
        requestServices,
        configuration,
        rover: null,
        maxPhotos: null,
        (pipeline, token) => pipeline.RunAsync(lines, token),
        cancellationToken);

    return Results.Ok(report);
});

app.Run();

/// <summary>
/// Resolves the pipeline and runs it, applying the optional per-request rover and photo-count
/// overrides.
/// </summary>
static async Task<RunReport> ExecuteAsync(
    IServiceProvider requestServices,
    IConfiguration configuration,
    string? rover,
    int? maxPhotos,
    Func<IPhotoPipeline, CancellationToken, Task<RunReport>> run,
    CancellationToken cancellationToken)
{
    var hasOverrides = !string.IsNullOrWhiteSpace(rover) || maxPhotos is not null;
    if (!hasOverrides)
    {
        return await run(requestServices.GetRequiredService<IPhotoPipeline>(), cancellationToken);
    }

    // Rover and photo count live in NasaOptions, which is bound once at startup. Mutating the shared
    // options object would leak across concurrent requests, so an override gets its own short-lived
    // container built from the same configuration plus an in-memory overlay. This costs one extra
    // container per overridden request, which only happens when the caller explicitly asks for it.
    var overlay = new Dictionary<string, string?>();
    if (!string.IsNullOrWhiteSpace(rover))
    {
        overlay[$"{NasaOptions.SectionName}:{nameof(NasaOptions.Rover)}"] = rover;
    }

    if (maxPhotos is { } limit)
    {
        overlay[$"{NasaOptions.SectionName}:{nameof(NasaOptions.MaxPhotosPerDate)}"] =
            limit.ToString(CultureInfo.InvariantCulture);
    }

    var scopedConfiguration = new ConfigurationBuilder()
        .AddConfiguration(configuration)
        .AddInMemoryCollection(overlay)
        .Build();

    var services = new ServiceCollection();
    // The child container cannot borrow the host logger factory without also owning its disposal,
    // so it gets its own console logging wired from the same Logging section.
    services.AddLogging(logging => logging
        .AddConfiguration(scopedConfiguration.GetSection("Logging"))
        .AddSimpleConsole());
    services.AddMarsRoverPhotos(scopedConfiguration);

    // ASP0000 warns about a second copy of the singletons, which is exactly the point here: this
    // container is built per overridden request and disposed with it, so nothing is shared with
    // the host. A run is a long, deliberate operation, so one extra container is cheap next to it.
#pragma warning disable ASP0000
    await using var provider = services.BuildServiceProvider();
#pragma warning restore ASP0000
    await using var scope = provider.CreateAsyncScope();
    return await run(scope.ServiceProvider.GetRequiredService<IPhotoPipeline>(), cancellationToken);
}

/// <summary>
/// A relative Input:DatesFilePath resolves against the content root, which is the project folder
/// under dotnet run while the linked dates.txt lands next to the built assembly. Probing the
/// assembly folder as well keeps GET /api/run working from a clean clone. Returns null whenever
/// the configured path already works, so the pipeline keeps its own resolution and error message.
/// </summary>
static string? ProbeConfiguredDatesFile(string contentRoot, string configured)
{
    if (string.IsNullOrWhiteSpace(configured) || Path.IsPathRooted(configured))
    {
        return null;
    }

    if (File.Exists(Path.Combine(contentRoot, configured)))
    {
        return null;
    }

    var besideTheAssembly = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
    return File.Exists(besideTheAssembly) ? besideTheAssembly : null;
}

/// <summary>Guards against a caller pointing the dates file at anything outside the content root.</summary>
static bool TryResolveInsideContentRoot(string contentRoot, string candidate, out string resolved)
{
    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
    resolved = Path.GetFullPath(Path.Combine(root, candidate));

    var comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    return resolved.StartsWith(root + Path.DirectorySeparatorChar, comparison);
}

/// <summary>Body of POST /api/run.</summary>
internal sealed record RunRequest(IReadOnlyList<string>? Dates);

/// <summary>Keeps DateOnly on the wire as yyyy-MM-dd in both directions.</summary>
internal sealed class IsoDateOnlyConverter : JsonConverter<DateOnly>
{
    private const string Format = "yyyy-MM-dd";

    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateOnly.ParseExact(reader.GetString()!, Format, CultureInfo.InvariantCulture);

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
}
