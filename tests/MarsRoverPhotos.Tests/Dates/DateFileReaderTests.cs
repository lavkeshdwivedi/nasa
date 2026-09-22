using MarsRoverPhotos.Core.Dates;

namespace MarsRoverPhotos.Tests.Dates;

public sealed class DateFileReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    [Fact]
    public async Task ReadAsync_ReadsEveryDateLineInOrder()
    {
        var path = WriteFile("02/27/17", "June 2, 2018", "Jul-13-2016");

        var lines = await new DateFileReader().ReadAsync(path);

        Assert.Equal(3, lines.Count);
        Assert.Equal(new[] { "02/27/17", "June 2, 2018", "Jul-13-2016" }, lines.Select(line => line.Value));
        Assert.Equal(new[] { 1, 2, 3 }, lines.Select(line => line.LineNumber));
    }

    [Fact]
    public async Task ReadAsync_KeepsTrueLineNumbersAcrossBlankAndCommentLines()
    {
        var path = WriteFile("", "# the dates we care about", "  02/27/17  ", "   ", "June 2, 2018");

        var lines = await new DateFileReader().ReadAsync(path);

        Assert.Equal(2, lines.Count);
        Assert.Equal(3, lines[0].LineNumber);
        Assert.Equal("02/27/17", lines[0].Value);
        Assert.Equal(5, lines[1].LineNumber);
        Assert.Equal("June 2, 2018", lines[1].Value);
    }

    [Fact]
    public async Task ReadAsync_TreatsAnIndentedHashAsAComment()
    {
        var path = WriteFile("   # indented note", "02/27/17");

        var lines = await new DateFileReader().ReadAsync(path);

        Assert.Equal("02/27/17", Assert.Single(lines).Value);
    }

    [Fact]
    public async Task ReadAsync_ReturnsEmptyListWhenTheFileHasNothingUsable()
    {
        var path = WriteFile("", "# nothing here", "   ");

        var lines = await new DateFileReader().ReadAsync(path);

        Assert.Empty(lines);
    }

    [Fact]
    public async Task ReadAsync_ThrowsFileNotFoundNamingTheFullPath()
    {
        var missing = Path.Combine(_root, "no-such-file.txt");

        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => new DateFileReader().ReadAsync(missing));

        Assert.Contains(Path.GetFullPath(missing), exception.Message);
        Assert.Equal(missing, exception.FileName);
    }

    [Fact]
    public async Task ReadAsync_HonoursCancellation()
    {
        var path = WriteFile("02/27/17", "June 2, 2018");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DateFileReader().ReadAsync(path, cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(params string[] lines)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "dates.txt");
        File.WriteAllLines(path, lines);
        return path;
    }
}
