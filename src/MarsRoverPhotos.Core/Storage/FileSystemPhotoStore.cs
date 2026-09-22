using System.Globalization;
using System.Text;
using MarsRoverPhotos.Core.Abstractions;
using MarsRoverPhotos.Core.Models;
using MarsRoverPhotos.Core.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarsRoverPhotos.Core.Storage;

/// <summary>
/// Stores photos as {RootPath}/yyyy-MM-dd/{photoId}_{camera}.{ext}, writing through a temp file
/// so an interrupted run never leaves something that looks like a complete cached photo.
/// </summary>
public sealed class FileSystemPhotoStore : IPhotoStore
{
    private const string DefaultExtension = "jpg";
    private const string FallbackCameraName = "camera";

    private readonly string _rootPath;
    private readonly ILogger<FileSystemPhotoStore> _logger;

    public FileSystemPhotoStore(IOptions<StorageOptions> options, ILogger<FileSystemPhotoStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var configuredRoot = options.Value.RootPath;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            configuredRoot = new StorageOptions().RootPath;
        }

        // Resolved once against the process current directory: every later path is built from this
        // absolute root, so a change of working directory mid-run cannot move the output folder.
        _rootPath = Path.GetFullPath(configuredRoot);
    }

    public string RootPath => _rootPath;

    public string GetFolderPath(DateOnly earthDate) =>
        Path.Combine(_rootPath, earthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public string GetPhotoPath(MarsPhoto photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        return Path.Combine(GetFolderPath(photo.EarthDate), BuildFileName(photo));
    }

    /// <summary>
    /// A zero-length file is treated as absent. A previous run that died mid-write, or a disk that
    /// filled up, can leave an empty file behind, and calling that "cached" would silently lose the photo.
    /// </summary>
    public bool Exists(MarsPhoto photo)
    {
        var info = new FileInfo(GetPhotoPath(photo));
        return info.Exists && info.Length > 0;
    }

    public async Task<string> SaveAsync(MarsPhoto photo, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentNullException.ThrowIfNull(content);

        var finalPath = GetPhotoPath(photo);
        var folder = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(folder);

        // Sibling temp name, so the move below stays on the same volume and is therefore atomic
        // on the platforms we care about. The GUID keeps two concurrent writers from colliding.
        var tempPath = Path.Combine(folder, $"{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.partial");

        try
        {
            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // Checked after the stream is closed: a cancellation that lands on the last chunk must not
            // promote a half written temp file into the final path.
            cancellationToken.ThrowIfCancellationRequested();

            File.Move(tempPath, finalPath, overwrite: true);
            _logger.LogDebug("Saved photo {PhotoId} to {Path}", photo.Id, finalPath);
            return finalPath;
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private static string BuildFileName(MarsPhoto photo) =>
        $"{photo.Id}_{SanitiseCameraName(photo.CameraName)}.{ResolveExtension(photo.ImageSourceUrl)}";

    /// <summary>
    /// The camera name comes from the API, so it is untrusted. Building a path by concatenating it
    /// raw would let a value such as "../../etc" write outside the date folder, so every separator,
    /// dot and platform-invalid character is replaced before Path.Combine ever sees it.
    /// </summary>
    private static string SanitiseCameraName(string? cameraName)
    {
        if (string.IsNullOrWhiteSpace(cameraName))
        {
            return FallbackCameraName;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(cameraName.Length);

        foreach (var c in cameraName)
        {
            // A backslash is a legal file name character on Linux, and '.' enables "..", so neither is
            // covered by GetInvalidFileNameChars everywhere. Both are excluded explicitly.
            var unsafeChar = c == '/' || c == '\\' || c == '.' || c == ':' || char.IsControl(c)
                             || Array.IndexOf(invalid, c) >= 0;
            builder.Append(unsafeChar ? '_' : c);
        }

        var sanitised = builder.ToString().Trim();
        return sanitised.Length == 0 ? FallbackCameraName : sanitised;
    }

    /// <summary>Takes the extension from the image URL path, falling back to jpg when there is none.</summary>
    private static string ResolveExtension(Uri imageSourceUrl)
    {
        // AbsolutePath deliberately, not the whole URL: a query string must not become the extension.
        var extension = Path.GetExtension(imageSourceUrl.AbsolutePath).TrimStart('.');
        if (extension.Length == 0 || extension.Length > 8 || !extension.All(char.IsLetterOrDigit))
        {
            return DefaultExtension;
        }

        return extension.ToLowerInvariant();
    }

    private void TryDeleteTempFile(string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort: the original failure is the interesting one and is rethrown by the caller.
            _logger.LogDebug(ex, "Could not delete temp file {TempPath}", tempPath);
        }
    }
}
