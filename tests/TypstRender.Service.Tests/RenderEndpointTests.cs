using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using TypstRender.Contracts;
using Xunit;

namespace TypstRender.Service.Tests;

public sealed class RenderEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RenderEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private const string HelloTemplate =
        "#let d = json(sys.inputs.at(\"data-path\"))\n" +
        "#set page(width: 120pt, height: 60pt)\n" +
        "Hello #d.name";

    private static readonly string RenderUrl =
        $"{RenderProtocol.RenderPath}?{RenderProtocol.EntryQueryParam}=main.typ" +
        $"&{RenderProtocol.InputQueryParam}={Uri.EscapeDataString(RenderProtocol.DataPathInputKey + "=/" + RenderProtocol.DataFileName)}";

    /// <summary>
    /// A factory with <c>Render</c> settings overridden, so limit behaviour
    /// (timeouts, capacity, a broken binary) can be exercised at all.
    /// </summary>
    private WebApplicationFactory<Program> WithSettings(params (string Key, string Value)[] settings)
        => _factory.WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(RenderProtocol.HealthPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ready_WithTypstOnPath_ReportsTheVersion()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(RenderProtocol.ReadyPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("typst", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ready_WithoutARunnableBinary_Returns503()
    {
        // An image whose typst install is missing or built for another
        // architecture used to report itself healthy and fail every render.
        using var factory = WithSettings(("Render:TypstBinaryPath", "/nonexistent/typst"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(RenderProtocol.ReadyPath);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Render_ValidBundle_ReturnsPdf()
    {
        using var client = _factory.CreateClient();
        using var content = ZipContent(new()
        {
            ["main.typ"] = HelloTemplate,
            ["data.json"] = "{\"name\":\"World\"}",
        });

        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task Render_WithoutAnEntryParam_FallsBackToMainTyp()
    {
        using var client = _factory.CreateClient();
        using var content = ZipContent(new() { ["main.typ"] = "#set page(width: 60pt, height: 60pt)\n= Hi" });

        using var response = await client.PostAsync(RenderProtocol.RenderPath, content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Render_NestedEntryWithRootAbsoluteImport_Resolves()
    {
        // The headline capability — a nested entry importing /shared/... — had no
        // service-side coverage at all.
        using var client = _factory.CreateClient();
        using var content = ZipContent(new()
        {
            ["shared/styles.typ"] = "#let primary = rgb(\"#102030\")",
            ["docs/intro/main.typ"] =
                "#import \"/shared/styles.typ\": primary\n#set page(width: 80pt, height: 60pt)\n#text(fill: primary)[Hi]",
        });

        using var response = await client.PostAsync(
            $"{RenderProtocol.RenderPath}?{RenderProtocol.EntryQueryParam}={Uri.EscapeDataString("docs/intro/main.typ")}",
            content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Render_MalformedTemplate_Returns422()
    {
        using var client = _factory.CreateClient();
        using var content = ZipContent(new() { ["main.typ"] = "#this is not valid typst {{{" });

        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
    }

    [Fact]
    public async Task Render_CompileError_DoesNotLeakTheTempWorkDirectory()
    {
        // Typst prints absolute paths for a missing file; the per-render temp
        // directory is an internal detail that must not reach the caller.
        using var client = _factory.CreateClient();
        using var content = ZipContent(new() { ["main.typ"] = "#read(\"/etc/hostname\")" });

        using var response = await client.PostAsync(RenderUrl, content);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("typst-render", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_ZipSlip_Returns400()
    {
        using var client = _factory.CreateClient();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../escape.typ");
            using var s = entry.Open();
            s.Write("evil"u8);
        }
        using var content = ZipBytesContent(ms.ToArray());

        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_NotAZip_Returns400()
    {
        using var client = _factory.CreateClient();
        using var content = ZipBytesContent(Encoding.ASCII.GetBytes("this is not a zip archive"));

        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_EntryNameCollidingWithADirectory_Returns400NotAServerError()
    {
        // A zip carrying both a file "a" and a file "a/b" makes the second
        // CreateDirectory throw IOException; that used to surface as a 500 with
        // an empty body (or a stack trace in Development).
        using var client = _factory.CreateClient();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "a", "a/b" })
            {
                using var s = archive.CreateEntry(name).Open();
                s.Write("x"u8);
            }
        }

        using var content = ZipBytesContent(ms.ToArray());
        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_BundleOverTheEntryLimit_Returns400()
    {
        using var factory = WithSettings(("Render:MaxBundleEntries", "3"));
        using var client = factory.CreateClient();
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 10; i++)
            {
                using var s = archive.CreateEntry($"f{i}.txt").Open();
                s.Write("x"u8);
            }
        }

        using var content = ZipBytesContent(ms.ToArray());
        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("more than 3 files", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_BundleUnpackingBeyondTheByteBudget_Returns400()
    {
        // MaxUploadBytes caps only the compressed body: 1 MiB of zeros is a few
        // hundred bytes zipped, and a real bomb expands a thousandfold. Both
        // limits are set here because MaxExtractedBytes may not be the smaller.
        using var factory = WithSettings(
            ("Render:MaxUploadBytes", "65536"),
            ("Render:MaxExtractedBytes", "65536"));
        using var client = factory.CreateClient();

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var s = archive.CreateEntry("main.typ", CompressionLevel.Optimal).Open();
            s.Write(new byte[1024 * 1024]);
        }

        using var content = ZipBytesContent(ms.ToArray());
        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unpacks to more than", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_MissingEntry_Returns400()
    {
        using var client = _factory.CreateClient();
        using var content = ZipContent(new() { ["other.typ"] = "= hi" });

        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_MalformedInputPair_Returns400WithoutInvokingTypst()
    {
        // typst answers a pair without '=' with a clap usage dump and exit code
        // 2, which surfaced as a 422 "compile error" for a caller mistake.
        using var client = _factory.CreateClient();
        using var content = ZipContent(new() { ["main.typ"] = "= hi" });

        using var response = await client.PostAsync(
            $"{RenderProtocol.RenderPath}?{RenderProtocol.InputQueryParam}=nokeyvalue", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("key=value", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_RepeatedEntryParam_Returns400()
    {
        using var client = _factory.CreateClient();
        using var content = ZipContent(new() { ["main.typ"] = "= hi" });

        using var response = await client.PostAsync(
            $"{RenderProtocol.RenderPath}?{RenderProtocol.EntryQueryParam}=a.typ"
                + $"&{RenderProtocol.EntryQueryParam}=b.typ",
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_WithoutARunnableBinary_Returns500()
    {
        using var factory = WithSettings(("Render:TypstBinaryPath", "/nonexistent/typst"));
        using var client = factory.CreateClient();
        using var content = ZipContent(new() { ["main.typ"] = "= hi" });

        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("typst", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_AtCapacity_Returns503()
    {
        using var factory = WithSettings(
            ("Render:MaxConcurrency", "1"),
            ("Render:QueueTimeoutSeconds", "0"));
        using var client = factory.CreateClient();

        // A page loop slow enough that the second request is still queued.
        const string slow = "#set page(width: 200pt, height: 200pt)\n"
            + "#for i in range(4000) [ #text(size: 8pt)[line #i] #linebreak() ]";

        var first = client.PostAsync(RenderUrl, ZipContent(new() { ["main.typ"] = slow }));
        var second = client.PostAsync(RenderUrl, ZipContent(new() { ["main.typ"] = slow }));

        var responses = await Task.WhenAll(first, second);
        try
        {
            Assert.Contains(HttpStatusCode.ServiceUnavailable, responses.Select(r => r.StatusCode));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Render_ExceedingTheRenderTimeout_Returns504()
    {
        using var factory = WithSettings(
            ("Render:TimeoutSeconds", "1"),
            ("Render:ShutdownTimeoutSeconds", "5"));
        using var client = factory.CreateClient();

        const string slow = "#set page(width: 400pt, height: 400pt)\n"
            + "#for i in range(80000) [ #text(size: 6pt)[line #i] #linebreak() ]";

        using var response = await client.PostAsync(RenderUrl, ZipContent(new() { ["main.typ"] = slow }));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task Render_Success_LeavesNoWorkDirectoryBehind()
    {
        var workRoot = Path.Combine(Path.GetTempPath(), "typst-render");
        var before = SnapshotWorkspaces(workRoot);

        using var client = _factory.CreateClient();
        using var content = ZipContent(new()
        {
            ["main.typ"] = HelloTemplate,
            ["data.json"] = "{\"name\":\"World\"}",
        });
        using var response = await client.PostAsync(RenderUrl, content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(SnapshotWorkspaces(workRoot).Except(before, StringComparer.Ordinal));
    }

    private static HashSet<string> SnapshotWorkspaces(string workRoot)
        => Directory.Exists(workRoot)
            ? new HashSet<string>(Directory.EnumerateDirectories(workRoot), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    private static ByteArrayContent ZipContent(Dictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, text) in files)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var s = entry.Open();
                s.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return ZipBytesContent(ms.ToArray());
    }

    private static ByteArrayContent ZipBytesContent(byte[] zip)
    {
        var content = new ByteArrayContent(zip);
        content.Headers.ContentType = new MediaTypeHeaderValue(RenderProtocol.BundleContentType);
        return content;
    }
}
