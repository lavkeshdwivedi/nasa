using System.Text;
using MarsRoverPhotos.Core.Models;
using MarsRoverPhotos.Core.Options;
using MarsRoverPhotos.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarsRoverPhotos.Tests.Storage;

public sealed class FileSystemPhotoStoreTests : IDisposable
{
    private const string SampleUrl =
        "https://mars.nasa.gov/msl-raw-images/proj/msl/redops/FRB_486265257EDR_F0481570FHAZ00323M_.JPG";

    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [Fact]
    public void GetFolderPath_PutsEachEarthDateInItsOwnDatedFolder()
    {
        var store = CreateStore();

        var folder = store.GetFolderPath(new DateOnly(2017, 2, 27));

        Assert.Equal(Path.Combine(_root, "2017-02-27"), folder);
    }

    [Fact]
    public void GetPhotoPath_NamesFileFromIdCameraAndUrlExtension()
    {
        var store = CreateStore();

        var path = store.GetPhotoPath(CreatePhoto());

        Assert.Equal(Path.Combine(_root, "2017-02-27", "424905_FHAZ.jpg"), path);
    }

    [Fact]
    public void GetPhotoPath_KeepsNonJpegExtensionFromUrl()
    {
        var store = CreateStore();

        var path = store.GetPhotoPath(CreatePhoto(url: "https://example.test/images/rover.png"));

        Assert.Equal("424905_FHAZ.png", Path.GetFileName(path));
    }

    [Fact]
    public void GetPhotoPath_DefaultsToJpg_WhenUrlHasNoExtension()
    {
        var store = CreateStore();

        var path = store.GetPhotoPath(CreatePhoto(url: "https://example.test/images/rover?id=7"));

        Assert.Equal("424905_FHAZ.jpg", Path.GetFileName(path));
    }

    [Fact]
    public void GetPhotoPath_SanitisesCameraName_SoAHostileValueCannotEscapeTheDateFolder()
    {
        var store = CreateStore();

        var path = store.GetPhotoPath(CreatePhoto(camera: "../../etc/passwd"));

        var fileName = Path.GetFileName(path);
        Assert.Equal(store.GetFolderPath(new DateOnly(2017, 2, 27)), Path.GetDirectoryName(path));
        Assert.DoesNotContain("..", fileName);
        Assert.DoesNotContain('/', fileName);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, fileName);
        Assert.StartsWith("424905_", fileName);
        Assert.EndsWith(".jpg", fileName);
    }

    [Fact]
    public void RootPath_IsResolvedToAnAbsolutePath()
    {
        var store = new FileSystemPhotoStore(
            Options.Create(new StorageOptions { RootPath = "photos" }),
            NullLogger<FileSystemPhotoStore>.Instance);

        Assert.True(Path.IsPathFullyQualified(store.GetFolderPath(new DateOnly(2017, 2, 27))));
    }

    [Fact]
    public void Exists_IsFalse_WhenNothingHasBeenDownloaded()
    {
        var store = CreateStore();

        Assert.False(store.Exists(CreatePhoto()));
    }

    [Fact]
    public void Exists_IsFalse_ForAZeroByteFile_SoATruncatedLeftoverIsRetried()
    {
        var store = CreateStore();
        var photo = CreatePhoto();
        var path = store.GetPhotoPath(photo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);

        Assert.False(store.Exists(photo));
    }

    [Fact]
    public void Exists_IsTrue_WhenTheFileHasContent()
    {
        var store = CreateStore();
        var photo = CreatePhoto();
        var path = store.GetPhotoPath(photo);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.True(store.Exists(photo));
    }

    [Fact]
    public async Task SaveAsync_CreatesTheDateFolderAndWritesTheContent()
    {
        var store = CreateStore();
        var photo = CreatePhoto();
        var bytes = Encoding.UTF8.GetBytes("fake jpeg bytes");

        var path = await store.SaveAsync(photo, new MemoryStream(bytes), CancellationToken.None);

        Assert.Equal(store.GetPhotoPath(photo), path);
        Assert.True(File.Exists(path));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.True(store.Exists(photo));
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempFileBehind()
    {
        var store = CreateStore();
        var photo = CreatePhoto();

        var path = await store.SaveAsync(photo, new MemoryStream([9, 9, 9]), CancellationToken.None);

        var files = Directory.GetFiles(Path.GetDirectoryName(path)!);
        Assert.Equal(new[] { path }, files);
    }

    [Fact]
    public async Task SaveAsync_OverwritesAnExistingFile()
    {
        var store = CreateStore();
        var photo = CreatePhoto();
        await store.SaveAsync(photo, new MemoryStream([1]), CancellationToken.None);

        var path = await store.SaveAsync(photo, new MemoryStream([7, 7]), CancellationToken.None);

        Assert.Equal(new byte[] { 7, 7 }, await File.ReadAllBytesAsync(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
    }

    [Fact]
    public async Task SaveAsync_OnCancellation_LeavesNeitherAFinalNorATempFile()
    {
        var store = CreateStore();
        var photo = CreatePhoto();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(photo, new MemoryStream([1, 2, 3]), cts.Token));

        var folder = store.GetFolderPath(photo.EarthDate);
        Assert.False(store.Exists(photo));
        Assert.Empty(Directory.GetFiles(folder));
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

    private static MarsPhoto CreatePhoto(
        long id = 424905,
        string camera = "FHAZ",
        string url = SampleUrl,
        string earthDate = "2017-02-27") =>
        new(id, new Uri(url), DateOnly.ParseExact(earthDate, "yyyy-MM-dd"), "curiosity", camera);
}
