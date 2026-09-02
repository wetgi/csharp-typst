using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly Uri? _renderEndpoint;

    /// <summary>Creates a client over the given <see cref="HttpClient"/> and options.</summary>
    /// <remarks>
    /// Marked as the activator's constructor: the typed-client factory would
    /// otherwise see two two-parameter constructors and report them ambiguous.
    /// </remarks>
    [ActivatorUtilitiesConstructor]
    public TypstRenderClient(HttpClient http, IOptions<TypstRenderClientOptions> options)
        : this(http, (options ?? throw new ArgumentNullException(nameof(options))).Value)
    {
    }

    /// <summary>
    /// Creates a client over the given <see cref="HttpClient"/> and options,
    /// without going through <c>IOptions</c> — for a console app or a test that
    /// does not have a DI container.
    /// </summary>
    public TypstRenderClient(HttpClient http, TypstRenderClientOptions options)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        // Resolved once, and the injected HttpClient is left untouched: assigning
        // BaseAddress on a client that has already sent a request throws, so a
        // shared/static HttpClient used to make construction fail unpredictably.
        var baseAddress = _options.BaseAddress ?? _http.BaseAddress;
        _renderEndpoint = baseAddress is null ? null : BuildRenderEndpoint(baseAddress);
    }

    /// <inheritdoc />
    public Task<byte[]> RenderAsync(string entry, CancellationToken cancellationToken = default)
        => RenderAsync(entry, data: null, cancellationToken);

    /// <inheritdoc />
    public Task<byte[]> RenderAsync(string entry, object? data, CancellationToken cancellationToken = default)
        => RenderAsync(new TypstRenderRequest { Entry = entry, Data = data }, cancellationToken);

    /// <inheritdoc />
    public async Task<byte[]> RenderAsync(
        TypstRenderRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendRenderAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

#if NET5_0_OR_GREATER
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
#endif
    }

    /// <inheritdoc />
    public Task<Stream> RenderToStreamAsync(string entry, CancellationToken cancellationToken = default)
        => RenderToStreamAsync(entry, data: null, cancellationToken);

    /// <inheritdoc />
    public Task<Stream> RenderToStreamAsync(
        string entry, object? data, CancellationToken cancellationToken = default)
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
#if NET5_0_OR_GREATER
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
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

        var endpoint = _renderEndpoint ?? throw new InvalidOperationException(
            $"No render service address configured. Set {nameof(TypstRenderClientOptions)}."
                + $"{nameof(TypstRenderClientOptions.BaseAddress)} to the service URL, "
                + "e.g. new Uri(\"http://localhost:8080\").");

        var entry = TemplateScanner.NormalizeEntry(request.Entry);
        var dataJson = request.Data is null
            ? null
            : JsonSerializer.SerializeToUtf8Bytes(request.Data, JsonOptions);
        var extraFiles = NormalizeFileSet(
            request.ExtraFiles,
            $"{nameof(TypstRenderRequest)}.{nameof(TypstRenderRequest.ExtraFiles)}",
            rejectDataFile: dataJson is not null);

        var zipBytes = request.Files is not null
            ? BuildZip(NormalizeSuppliedFiles(request, entry, extraFiles), extraFiles, dataJson)
            : BundleFromDisk(request, entry, extraFiles, dataJson);

        var requestUri = new Uri(endpoint.AbsoluteUri + BuildQuery(entry, dataJson is not null, request.Inputs));

        using var content = new ByteArrayContent(zipBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(RenderProtocol.BundleContentType);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/pdf"));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(httpRequest, completionOption, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient reports its own timeout as a cancellation, which is
            // indistinguishable from the caller cancelling unless we check.
            throw new TypstRenderException(
                0,
                $"Rendering '{entry}' timed out after {_http.Timeout}. Raise "
                    + $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.Timeout)} "
                    + "if the document legitimately takes longer.",
                detail: null,
                entry: entry,
                requestUri: requestUri,
                innerException: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TypstRenderException(
                0,
                $"Could not reach the Typst render service at '{requestUri}'.",
                detail: ex.Message,
                entry: entry,
                requestUri: requestUri,
                innerException: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var statusCode = (int)response.StatusCode;
        var reason = response.ReasonPhrase;
#if NET5_0_OR_GREATER
        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
#else
        var detail = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
#endif
        response.Dispose();

        throw new TypstRenderException(
            statusCode,
            $"Rendering '{entry}' failed with status {statusCode}"
                + (string.IsNullOrEmpty(reason) ? "." : $" ({reason})."),
            string.IsNullOrWhiteSpace(detail) ? null : detail,
            entry: entry,
            requestUri: requestUri);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTemplates()
    {
        var root = RequireTemplateRoot(_options.TemplateRoot);

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
        var entryRel = TemplateScanner.NormalizeEntry(entry);
        var root = RequireTemplateRoot(_options.TemplateRoot);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Template root not found: '{root}'.");
        }

        var extraPaths = extraFiles?
            .Select(NormalizePath)
            .ToList();
        var scan = TemplateScanner.Scan(root, entryRel, bundleMode ?? _options.BundleMode, extraPaths);
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

    private static string RequireTemplateRoot(string? root)
        => root ?? throw new InvalidOperationException(
            $"No template root configured. Set {nameof(TypstRenderClientOptions)}."
                + $"{nameof(TypstRenderClientOptions.TemplateRoot)}. A relative path resolves against "
                + "the process working directory, which differs under IIS and in a container — "
                + "consider Path.Combine(AppContext.BaseDirectory, \"templates\").");

    /// <summary>
    /// Normalizes bundle paths to root-relative '/'-separated form and rejects
    /// ones that cannot name a file inside the bundle.
    /// </summary>
    private static IReadOnlyDictionary<string, byte[]> NormalizeFileSet(
        IEnumerable<KeyValuePair<string, byte[]>> files, string parameterName, bool rejectDataFile)
    {
        Dictionary<string, byte[]>? normalized = null;

        foreach (var kvp in files)
        {
            var path = NormalizePath(kvp.Key);
            if (path.Length == 0)
            {
                throw new ArgumentException($"{parameterName} paths must be non-empty.", parameterName);
            }

            if (path.Split('/').Contains(".."))
            {
                throw new ArgumentException(
                    $"{parameterName} path '{kvp.Key}' must stay inside the bundle, without '..'.",
                    parameterName);
            }

            if (rejectDataFile && string.Equals(path, RenderProtocol.DataFileName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"{parameterName} path '{kvp.Key}' collides with the conventional "
                        + $"'{RenderProtocol.DataFileName}' written for "
                        + $"{nameof(TypstRenderRequest)}.{nameof(TypstRenderRequest.Data)}.",
                    parameterName);
            }

            normalized ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
            normalized[path] = kvp.Value;
        }

        return normalized ?? EmptyFiles;
    }

    /// <summary>
    /// Normalizes an in-memory bundle's keys the same way as everything else — a
    /// Windows caller building keys with <c>Path.Combine</c> would otherwise ship
    /// entries literally named <c>invoice\main.typ</c> — and fails fast when the
    /// entry is not among them, instead of after a round-trip.
    /// </summary>
    private static IReadOnlyDictionary<string, byte[]> NormalizeSuppliedFiles(
        TypstRenderRequest request, string entry, IReadOnlyDictionary<string, byte[]> extraFiles)
    {
        var files = NormalizeFileSet(
            request.Files!,
            $"{nameof(TypstRenderRequest)}.{nameof(TypstRenderRequest.Files)}",
            rejectDataFile: false);

        if (!files.ContainsKey(entry) && !extraFiles.ContainsKey(entry))
        {
            throw new ArgumentException(
                $"Entry '{entry}' is not present in {nameof(TypstRenderRequest)}."
                    + $"{nameof(TypstRenderRequest.Files)}. Supplied: {string.Join(", ", files.Keys)}",
                nameof(request));
        }

        return files;
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

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
            if (string.IsNullOrEmpty(kvp.Key))
            {
                throw new ArgumentException(
                    $"{nameof(TypstRenderRequest)}.{nameof(TypstRenderRequest.Inputs)} keys must be non-empty.",
                    nameof(inputs));
            }

            if (hasData && string.Equals(kvp.Key, RenderProtocol.DataPathInputKey, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Input '{RenderProtocol.DataPathInputKey}' is set by the client for "
                        + $"{nameof(TypstRenderRequest)}.{nameof(TypstRenderRequest.Data)}; supplying it as well "
                        + "would pass two conflicting values to typst.",
                    nameof(inputs));
            }

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
        using var ms = new MemoryStream(templateZip.Length + 4096);
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
    /// Resolves the absolute <c>POST /render</c> URI once, so no request has to
    /// rely on relative-URI resolution against a mutable
    /// <see cref="HttpClient.BaseAddress"/>.
    /// </summary>
    private static Uri BuildRenderEndpoint(Uri baseAddress)
    {
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException(
                $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.BaseAddress)} "
                    + $"must be an absolute URI, e.g. new Uri(\"http://localhost:8080\"); got '{baseAddress}'.",
                nameof(baseAddress));
        }

        if (!string.IsNullOrEmpty(baseAddress.Query) || !string.IsNullOrEmpty(baseAddress.Fragment))
        {
            throw new ArgumentException(
                $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.BaseAddress)} "
                    + $"must not carry a query string or fragment; got '{baseAddress}'.",
                nameof(baseAddress));
        }

        // GetLeftPart, not AbsoluteUri: appending to the latter would put the
        // separator after a query string and silently drop the path prefix.
        var path = baseAddress.GetLeftPart(UriPartial.Path);
        if (!path.EndsWith("/", StringComparison.Ordinal))
        {
            path += "/";
        }

        return new Uri(path + RenderProtocol.RenderPath.TrimStart('/'), UriKind.Absolute);
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

        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            => _inner.CopyToAsync(destination, bufferSize, cancellationToken);

#if NET5_0_OR_GREATER
        // Without these the modern async path detours through the byte[]
        // overload, and `await using` falls back to a synchronous Dispose.
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            _response.Dispose();
            GC.SuppressFinalize(this);
        }
#endif

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
