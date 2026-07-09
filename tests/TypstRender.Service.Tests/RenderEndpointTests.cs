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

    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(RenderProtocol.HealthPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
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
    public async Task Render_MalformedTemplate_Returns422()
    {
        using var client = _factory.CreateClient();
        using var content = ZipContent(new() { ["main.typ"] = "#this is not valid typst {{{" });

        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(await response.Content.ReadAsStringAsync()));
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
    public async Task Render_MissingEntry_Returns400()
    {
        using var client = _factory.CreateClient();
        using var content = ZipContent(new() { ["other.typ"] = "= hi" });

        using var response = await client.PostAsync(RenderUrl, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

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
