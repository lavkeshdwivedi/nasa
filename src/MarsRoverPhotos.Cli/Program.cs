using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.DependencyInjection;
using MarsRoverPhotos.Core.Models;
using MarsRoverPhotos.Core.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarsRoverPhotos.Cli;

/// <summary>
/// Console entry point.
///
/// Exit codes:
///   0  every date parsed, fetched and downloaded without a single error.
///   1  the run completed but at least one date had a validation or API error.
///   2  the run could not complete at all: missing input file, bad configuration,
///      an unhandled fault, or a Ctrl+C cancellation.
/// </summary>
internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitCompletedWithErrors = 1;
    private const int ExitFatal = 2;

    /// <summary>
    /// Declarative argv handling. Anything not listed here can still be set with the raw
    /// configuration key form, for example --Storage:MaxParallelDownloads=8.
    /// </summary>
    private static readonly Dictionary<string, string> SwitchMappings = new()
    {
        ["--dates-file"] = $"{InputOptions.SectionName}:{nameof(InputOptions.DatesFilePath)}",
        ["-f"] = $"{InputOptions.SectionName}:{nameof(InputOptions.DatesFilePath)}",
        ["--rover"] = $"{NasaOptions.SectionName}:{nameof(NasaOptions.Rover)}",
        ["-r"] = $"{NasaOptions.SectionName}:{nameof(NasaOptions.Rover)}",
        ["--max-photos"] = $"{NasaOptions.SectionName}:{nameof(NasaOptions.MaxPhotosPerDate)}",
        ["-n"] = $"{NasaOptions.SectionName}:{nameof(NasaOptions.MaxPhotosPerDate)}",
    };

    private static async Task<int> Main(string[] args)
    {
        if (args.Any(a => a is "--help" or "-h" or "-?" or "/?"))
        {
            PrintUsage();
            return ExitSuccess;
        }

        using var cancellation = new CancellationTokenSource();

        // First Ctrl+C asks the in-flight run to unwind. Setting Cancel keeps the process
        // alive long enough for that, so a second press stays the user's escape hatch.
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            if (cancellation.IsCancellationRequested)
            {
                return;
            }

            e.Cancel = true;
            Console.Error.WriteLine("Cancelling, waiting for in-flight downloads to stop...");
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            return await RunAsync(args, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Run cancelled.");
            return ExitFatal;
        }
        catch (FileNotFoundException ex)
        {
            // The dates file is the one input a user is most likely to get wrong.
            Console.Error.WriteLine($"Input file not found: {ex.FileName ?? ex.Message}");
            return ExitFatal;
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.Error.WriteLine($"Input path not found: {ex.Message}");
            return ExitFatal;
        }
        catch (Exception ex)
        {
            // A stack trace is noise for a CLI user. The logger already captured the detail.
            Console.Error.WriteLine($"Fatal: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is { } inner)
            {
                Console.Error.WriteLine($"  caused by {inner.GetType().Name}: {inner.Message}");
            }

            return ExitFatal;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        // Content root is pinned to the executable folder so appsettings.json and the copied
        // dates.txt are found regardless of the caller's working directory.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = Array.Empty<string>(),
        });

        // Precedence, lowest to highest: appsettings.json, appsettings.{Environment}.json,
        // environment variables, the bare positional path, then explicit command line switches.
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(AmbientOverrides(args))
            .AddCommandLine(args, SwitchMappings);

        // Relative paths only mean something once anchored, and the anchor differs between a
        // dev run out of bin/Debug/net9.0 and a published folder.
        var anchor = FindRepositoryRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;

        // The checked-in default ("dates.txt" from appsettings.json) is anchored to the repo root,
        // since that is where the file actually lives regardless of the bin/Debug output folder.
        // A path the user typed themselves (-f/--dates-file or the bare positional) is anchored to
        // the shell's own current directory instead, matching what every other CLI does with a
        // relative path: it means what the user is looking at, not where the app happens to live.
        var datesPathAnchor = HasExplicitDatesFileArgument(args) ? Environment.CurrentDirectory : anchor;
        builder.Configuration.AddInMemoryCollection(AbsolutePathOverrides(builder.Configuration, anchor, datesPathAnchor));

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });

        // Every Core service, including IPhotoPipeline, is registered by Core itself.
        builder.Services.AddMarsRoverPhotos(builder.Configuration);

        using var host = builder.Build();

        var pipeline = host.Services.GetRequiredService<IPhotoPipeline>();
        var report = await pipeline.RunAsync(cancellationToken: cancellationToken);

        var outputRoot = builder.Configuration[$"{StorageOptions.SectionName}:{nameof(StorageOptions.RootPath)}"];
        new ConsoleReportWriter().Write(report, outputRoot);

        return HasErrors(report) ? ExitCompletedWithErrors : ExitSuccess;
    }

    private static bool HasErrors(RunReport report) =>
        report.Results.Any(r => !r.IsValid || r.Errors.Count > 0 || r.PhotosFailed > 0);

    /// <summary>
    /// Two conveniences that sit just below the explicit switches: NASA_API_KEY, which is the
    /// documented home for the real key, and a bare positional path for the dates file.
    /// </summary>
    private static Dictionary<string, string?> AmbientOverrides(string[] args)
    {
        var overrides = new Dictionary<string, string?>();

        var apiKey = Environment.GetEnvironmentVariable("NASA_API_KEY");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            overrides[$"{NasaOptions.SectionName}:{nameof(NasaOptions.ApiKey)}"] = apiKey;
        }

        if (FindPositionalArgument(args) is { } positional)
        {
            overrides[$"{InputOptions.SectionName}:{nameof(InputOptions.DatesFilePath)}"] = positional;
        }

        return overrides;
    }

    private static Dictionary<string, string?> AbsolutePathOverrides(IConfiguration configuration, string anchor, string datesPathAnchor)
    {
        var datesKey = $"{InputOptions.SectionName}:{nameof(InputOptions.DatesFilePath)}";
        var rootKey = $"{StorageOptions.SectionName}:{nameof(StorageOptions.RootPath)}";

        var datesPath = configuration[datesKey] ?? "dates.txt";
        var rootPath = configuration[rootKey] ?? "photos";

        return new Dictionary<string, string?>
        {
            [datesKey] = Anchor(datesPath, datesPathAnchor),
            [rootKey] = Anchor(rootPath, anchor),
        };
    }

    private static string Anchor(string path, string anchor) =>
        Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(anchor, path));

    /// <summary>
    /// Walks up looking for the solution file. Returns null in a published layout, where the
    /// executable folder is the right anchor anyway.
    /// </summary>
    private static string? FindRepositoryRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("*.sln").Any() || dir.EnumerateFiles("*.slnx").Any())
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// AddCommandLine ignores bare arguments, so the optional positional path is picked out
    /// here. Tokens consumed as a space separated switch value are skipped.
    /// </summary>
    private static string? FindPositionalArgument(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith('-') || arg.StartsWith('/'))
            {
                // "--rover=curiosity" carries its value inline, "--rover curiosity" eats the next token.
                if (!arg.Contains('='))
                {
                    i++;
                }

                continue;
            }

            return arg;
        }

        return null;
    }

    /// <summary>
    /// True when the user named a dates file themselves, either as the bare positional argument
    /// or via -f/--dates-file in either "-f value" or "-f=value" form. Distinguishes an explicit
    /// choice, which should resolve relative to the shell's directory, from the appsettings.json
    /// default, which should resolve relative to the repository.
    /// </summary>
    private static bool HasExplicitDatesFileArgument(string[] args)
    {
        if (FindPositionalArgument(args) is not null)
        {
            return true;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--dates-file" or "-f")
            {
                return true;
            }

            if (arg.StartsWith("--dates-file=", StringComparison.Ordinal) || arg.StartsWith("-f=", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Mars Rover Photos

            Usage:
              MarsRoverPhotos.Cli [<dates-file>] [options]

            Options:
              -f, --dates-file <path>   Input file of dates, one per line. Overrides Input:DatesFilePath.
              -r, --rover <name>        Rover to query, for example curiosity. Overrides Nasa:Rover.
              -n, --max-photos <count>  Maximum photos to download per date. Overrides Nasa:MaxPhotosPerDate.
              -h, --help                Show this help.

            Any other setting can be given in raw configuration form, for example
            --Storage:MaxParallelDownloads=8.

            The NASA API key comes from the NASA_API_KEY environment variable or from
            dotnet user-secrets (Nasa:ApiKey). It is never read from a committed file.

            Exit codes: 0 all dates clean, 1 completed with errors, 2 fatal or cancelled.
            """);
    }
}
