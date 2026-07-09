using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TypstRender.Contracts;

namespace TypstRender.Client;

/// <inheritdoc />
public sealed class TypstRenderClient : ITypstRenderClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, byte[]> EmptyFiles = new Dictionary<string, byte[]>(0);

    // Templates change rarely; the zipped bundle is shared across client instances
    // (typed clients are transient) and invalidated by file metadata changes.
    private static readonly TemplateBundleCache BundleCache = new();

    private readonly HttpClient _http;
    private readonly TypstRenderClientOptions _options;

    /// <summary>Creates a client over the given <see cref="HttpClient"/> and options.</summary>
    public TypstRenderClient(HttpClient http, IOptions<TypstRenderClientOptions> options)
    {
        _http = http;
        _options = options.Value;

        if (_options.BaseAddress is not null && _http.BaseAddress is null)
        {
            _http.BaseAddress = _options.BaseAddress;
        }
    }

    /// <inheritdoc />
    public Task<byte[]> RenderAsync(
        string entry,
        object? data = null,
        CancellationToken cancellationToken = default)
        => RenderAsync(new TypstRenderRequest { Entry = entry, Data = data }, cancellationToken);

    /// <inheritdoc />
    public async Task<byte[]> RenderAsync(
        TypstRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendRenderAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<Stream> RenderToStreamAsync(
        string entry,
        object? data = null,
        CancellationToken cancellationToken = default)
        => RenderToStreamAsync(new TypstRenderRequest { Entry = entry, Data = data }, cancellationToken);

    /// <inheritdoc />
    public async Task<Stream> RenderToStreamAsync(
        TypstRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        // ResponseHeadersRead so the PDF body streams instead of buffering; the
        // returned stream owns the response and releases it when disposed.
        var response = await SendRenderAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return new ResponseStream(stream, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Builds the bundle and posts it, returning the (successful) response. The
    /// caller owns disposal of the returned response.
    /// </summary>
    private async Task<HttpResponseMessage> SendRenderAsync(
        TypstRenderRequest request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Entry))
        {
            throw new ArgumentException("Entry must name a .typ file inside the bundle.", nameof(request));
        }

        var entry = request.Entry.Replace('\\', '/').TrimStart('/');
        var dataJson = request.Data is null
            ? null
            : JsonSerializer.SerializeToUtf8Bytes(request.Data, JsonOptions);
        var extraFiles = NormalizeExtraFiles(request.ExtraFiles, hasData: dataJson is not null);

        var zipBytes = request.Files is not null
            ? BuildZip(request.Files, extraFiles, dataJson)
            : BundleFromDisk(request, entry, extraFiles, dataJson);

        using var content = new ByteArrayContent(zipBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(RenderProtocol.BundleContentType);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            RenderProtocol.RenderPath + BuildQuery(entry, dataJson is not null, request.Inputs))
        {
            Content = content,
        };

        var response = await _http.SendAsync(httpRequest, completionOption, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            response.Dispose();
            var message = $"Typst render failed with status {(int)response.StatusCode}.";
            throw new TypstRenderException((int)response.StatusCode, message, string.IsNullOrWhiteSpace(detail) ? null : detail);
        }

        return response;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTemplates()
    {
        var root = _options.TemplateRoot
            ?? throw new InvalidOperationException(
                $"No template root configured. Set {nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.TemplateRoot)}.");

        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        // Walk the whole tree: a template is any directory (at any depth) that
        // contains a main.typ. Names are root-relative, '/'-separated paths so a
        // grouped layout like invoice/paid + invoice/due is addressable as
        // "invoice/paid", which RenderAsync turns into "invoice/paid/main.typ".
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return Directory.EnumerateDirectories(fullRoot, "*", SearchOption.AllDirectories)
            .Where(d => File.Exists(Path.Combine(d, RenderProtocol.DefaultEntry)))
            .Select(d => d.Substring(fullRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/'))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public TemplateManifest GetBundleManifest(
        string entry, BundleMode? bundleMode = null, IEnumerable<string>? extraFiles = null)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new ArgumentException("Entry must name a .typ file inside the template root.", nameof(entry));
        }

        var root = _options.TemplateRoot
            ?? throw new InvalidOperationException(
                $"No template root configured. Set {nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.TemplateRoot)}.");

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Template root not found: '{root}'.");
        }

        var extraPaths = extraFiles?
            .Select(p => p.Replace('\\', '/').TrimStart('/'))
            .ToList();
        var scan = TemplateScanner.Scan(
            root, entry.Replace('\\', '/').TrimStart('/'), bundleMode ?? _options.BundleMode, extraPaths);
        return new TemplateManifest(scan.Files, scan.FullFolderReason);
    }

    private byte[] BundleFromDisk(
        TypstRenderRequest request, string entry, IReadOnlyDictionary<string, byte[]> extraFiles, byte[]? dataJson)
    {
        var root = request.TemplateRoot ?? _options.TemplateRoot
            ?? throw new InvalidOperationException(
                $"No template root configured. Set {nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.TemplateRoot)} " +
                $"or supply {nameof(TypstRenderRequest)}.{nameof(TypstRenderRequest.TemplateRoot)} / {nameof(TypstRenderRequest.Files)}.");

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Template root not found: '{root}'.");
        }

        var mode = request.BundleMode ?? _options.BundleMode;
        var templateZip = BundleCache.GetTemplateZip(root, entry, mode, extraFiles.Keys.ToList());
        return extraFiles.Count == 0 && dataJson is null
            ? templateZip
            : AppendFiles(templateZip, extraFiles, dataJson);
    }

    /// <summary>
    /// Normalizes <see cref="TypstRenderRequest.ExtraFiles"/> keys to root-relative
    /// '/'-separated paths and rejects a collision with the conventional data file.
    /// </summary>
    private static IReadOnlyDictionary<string, byte[]> NormalizeExtraFiles(
        IDictionary<string, byte[]> extraFiles, bool hasData)
    {
        if (extraFiles.Count == 0)
        {
            return EmptyFiles;
        }

        var normalized = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var kvp in extraFiles)
        {
            var path = kvp.Key.Replace('\\', '/').TrimStart('/');
            if (path.Length == 0)
            {
                throw new ArgumentException("Extra file paths must be non-empty.", nameof(extraFiles));
            }

            if (hasData && string.Equals(path, RenderProtocol.DataFileName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Extra file '{kvp.Key}' collides with the conventional '{RenderProtocol.DataFileName}' " +
                    $"written for {nameof(TypstRenderRequest)}.{nameof(TypstRenderRequest.Data)}.",
                    nameof(extraFiles));
            }

            normalized[path] = kvp.Value;
        }

        return normalized;
    }

    private static string BuildQuery(string entry, bool hasData, IDictionary<string, string> inputs)
    {
        var sb = new StringBuilder("?")
            .Append(RenderProtocol.EntryQueryParam).Append('=').Append(Uri.EscapeDataString(entry));

        if (hasData)
        {
            // Tell the template where to read its data, by convention.
            AppendInput(sb, RenderProtocol.DataPathInputKey, "/" + RenderProtocol.DataFileName);
        }

        foreach (var kvp in inputs)
        {
            AppendInput(sb, kvp.Key, kvp.Value);
        }

        return sb.ToString();
    }

    private static void AppendInput(StringBuilder sb, string key, string value)
        => sb.Append('&').Append(RenderProtocol.InputQueryParam).Append('=')
            .Append(Uri.EscapeDataString(key + "=" + value));

    /// <summary>
    /// Clones the cached template zip and overlays the per-request files (extra
    /// files and/or <c>data.json</c>) at their root-relative paths.
    /// </summary>
    private static byte[] AppendFiles(
        byte[] templateZip, IReadOnlyDictionary<string, byte[]> extraFiles, byte[]? dataJson)
    {
        var ms = new MemoryStream();
        ms.Write(templateZip, 0, templateZip.Length);

        using (var archive = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            // Update mode: an extra file may collide with a file already bundled
            // from disk (a checked-in local-preview placeholder), so replace
            // rather than append a duplicate entry.
            foreach (var kvp in extraFiles)
            {
                ReplaceEntry(archive, kvp.Key, kvp.Value);
            }

            if (dataJson is not null)
            {
                ReplaceEntry(archive, RenderProtocol.DataFileName, dataJson);
            }
        }

        return ms.ToArray();
    }

    private static byte[] BuildZip(
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyDictionary<string, byte[]> extraFiles,
        byte[]? dataJson)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // Create mode does not support reading archive.Entries (so ReplaceEntry
            // would throw); instead the colliding supplied files are filtered out
            // above, leaving each path to be written exactly once.
            foreach (var kvp in files)
            {
                if (extraFiles.ContainsKey(kvp.Key)
                    || (dataJson is not null && string.Equals(kvp.Key, RenderProtocol.DataFileName, StringComparison.Ordinal)))
                {
                    continue;
                }

                WriteEntry(archive, kvp.Key, kvp.Value);
            }

            foreach (var kvp in extraFiles)
            {
                WriteEntry(archive, kvp.Key, kvp.Value);
            }

            if (dataJson is not null)
            {
                WriteEntry(archive, RenderProtocol.DataFileName, dataJson);
            }
        }

        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, byte[] content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content, 0, content.Length);
    }

    private static void ReplaceEntry(ZipArchive archive, string path, byte[] content)
    {
        foreach (var entry in archive.Entries.Where(e => string.Equals(e.FullName, path, StringComparison.Ordinal)).ToList())
        {
            entry.Delete();
        }

        WriteEntry(archive, path, content);
    }

    /// <summary>
    /// Read-only stream over an HTTP response body that disposes the owning
    /// <see cref="HttpResponseMessage"/> when the caller disposes the stream, so
    /// the connection is released once the PDF has been consumed.
    /// </summary>
    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        private readonly Stream _inner = inner;
        private readonly HttpResponseMessage _response = response;

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
