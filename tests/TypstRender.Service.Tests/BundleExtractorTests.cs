using System.IO.Compression;
using TypstRender.Service.Render;
using Xunit;

namespace TypstRender.Service.Tests;

/// <summary>
/// Direct tests for the one component that handles wholly untrusted input.
/// Fast, and they need no typst binary — the HTTP-level tests only ever exercise
/// a single zip-slip variant.
/// </summary>
public sealed class BundleExtractorTests : IDisposable
{
    private static readonly BundleExtractor.Limits Generous = new(MaxEntries: 1000, MaxExtractedBytes: 10 * 1024 * 1024);

    private readonly string _dest;

    public BundleExtractorTests()
    {
        _dest = Path.Combine(Path.GetTempPath(), "typst-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dest);
    }

    public void Dispose() => Directory.Delete(_dest, recursive: true);

    [Fact]
    public async Task Extract_PlainBundle_WritesEveryFile()
    {
        using var zip = Zip(("main.typ", "= Hi"), ("shared/styles.typ", "#let x = 1"));

        var error = await BundleExtractor.ExtractAsync(zip, _dest, Generous, default);

        Assert.Null(error);
        Assert.Equal("= Hi", File.ReadAllText(Path.Combine(_dest, "main.typ")));
        Assert.Equal("#let x = 1", File.ReadAllText(Path.Combine(_dest, "shared", "styles.typ")));
    }

    [Theory]
    [InlineData("../escape.typ")]
    [InlineData("a/../../escape.typ")]
    public async Task Extract_EntryEscapingTheRoot_IsRejected(string name)
    {
        using var zip = Zip((name, "evil"));

        var error = await BundleExtractor.ExtractAsync(zip, _dest, Generous, default);

        Assert.NotNull(error);
        Assert.Contains("escapes the bundle root", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_AbsoluteEntryName_IsRejected()
    {
        using var zip = Zip(("/etc/passwd", "evil"));

        var error = await BundleExtractor.ExtractAsync(zip, _dest, Generous, default);

        Assert.NotNull(error);
    }

    [Fact]
    public async Task Extract_NotAZip_IsRejectedNotThrown()
    {
        using var zip = new MemoryStream("this is not a zip"u8.ToArray());

        var error = await BundleExtractor.ExtractAsync(zip, _dest, Generous, default);

        Assert.NotNull(error);
    }

    [Fact]
    public async Task Extract_MoreEntriesThanTheLimit_IsRejected()
    {
        using var zip = Zip(("a.typ", "a"), ("b.typ", "b"), ("c.typ", "c"));

        var error = await BundleExtractor.ExtractAsync(
            zip, _dest, new BundleExtractor.Limits(MaxEntries: 2, MaxExtractedBytes: 1024 * 1024), default);

        Assert.NotNull(error);
        Assert.Contains("more than 2 entries", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_DirectoryRecordsCountTowardTheEntryLimit()
    {
        using var zip = Zip(("a/", ""), ("b/", ""), ("c/", ""));

        var error = await BundleExtractor.ExtractAsync(
            zip, _dest, new BundleExtractor.Limits(MaxEntries: 2, MaxExtractedBytes: 1024), default);

        Assert.NotNull(error);
        Assert.Contains("more than 2 entries", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_UnpackingBeyondTheByteBudget_IsRejected()
    {
        // The compressed body is tiny; only the unpacked size is over budget.
        using var zip = Zip(("main.typ", new string('a', 200_000)));

        var error = await BundleExtractor.ExtractAsync(
            zip, _dest, new BundleExtractor.Limits(MaxEntries: 10, MaxExtractedBytes: 4096), default);

        Assert.NotNull(error);
        Assert.Contains("unpacks to more than", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_FileAndDirectoryNameClash_IsRejectedNotThrown()
    {
        // Entry order is attacker-controlled; creating the directory for "a/b"
        // fails once a file named "a" exists.
        using var zip = Zip(("a", "file"), ("a/b", "nested"));

        var error = await BundleExtractor.ExtractAsync(zip, _dest, Generous, default);

        Assert.NotNull(error);
    }

    [Fact]
    public async Task Extract_NormalizedDuplicateTarget_IsRejected()
    {
        using var zip = Zip(("main.typ", "first"), ("folder/../main.typ", "second"));

        var error = await BundleExtractor.ExtractAsync(zip, _dest, Generous, default);

        Assert.NotNull(error);
        Assert.Contains("duplicate target", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_HostFilesystemFailure_IsNotReportedAsInvalidInput()
    {
        File.WriteAllText(Path.Combine(_dest, "main.typ"), "already exists");
        using var zip = Zip(("main.typ", "replacement"));

        await Assert.ThrowsAsync<IOException>(
            () => BundleExtractor.ExtractAsync(zip, _dest, Generous, default));
    }

    [Theory]
    [InlineData("a/", "a", "conflicts with a directory")]
    [InlineData("a", "a/", "conflicts with a file")]
    [InlineData("a/b/", "a", "conflicts with a directory")]
    [InlineData("a", "a/b/", "file where a directory is required")]
    public async Task Extract_DirectoryAndFilePathClash_IsRejected(
        string first, string second, string expectedError)
    {
        // GetFullPath preserves a directory record's trailing separator. Both
        // exact and parent-path clashes must compare normalized targets.
        using var zip = Zip((first, ""), (second, ""));

        var error = await BundleExtractor.ExtractAsync(zip, _dest, Generous, default);

        Assert.NotNull(error);
        Assert.Contains(expectedError, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extract_CurrentDirectoryRecord_IsNotTreatedAsAnEscape()
    {
        // Some archivers emit a './' record for the root; trimming its
        // separator before the zip-slip check would read it as escaping.
        using var zip = Zip(("./", ""), ("main.typ", "hi"));

        var error = await BundleExtractor.ExtractAsync(zip, _dest, Generous, default);

        Assert.Null(error);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(_dest, "main.typ")));
    }

    [Fact]
    public async Task Extract_SymlinkEntry_NeverMaterializesALink()
    {
        // The bundle root is what typst is confined to, and typst follows a
        // symlink inside its root without complaint. .NET writes the link target
        // as ordinary file content rather than creating a link — this pins that
        // behaviour so a change to the extractor cannot open the hole silently.
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("secret.txt");
            // 0xA1FF: S_IFLNK | 0777, the Unix mode a zip tool records for a symlink.
            entry.ExternalAttributes = 0xA1FF << 16;
            using var s = entry.Open();
            s.Write("/etc/passwd"u8);
        }

        ms.Position = 0;
        var error = await BundleExtractor.ExtractAsync(ms, _dest, Generous, default);

        Assert.Null(error);
        var written = Path.Combine(_dest, "secret.txt");
        Assert.Null(File.ResolveLinkTarget(written, returnFinalTarget: false));
        Assert.Equal("/etc/passwd", File.ReadAllText(written));
    }

    private static MemoryStream Zip(params (string Path, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var s = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
                s.Write(System.Text.Encoding.UTF8.GetBytes(content));
            }
        }

        ms.Position = 0;
        return ms;
    }
}
