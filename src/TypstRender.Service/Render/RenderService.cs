using System.Diagnostics;
using Microsoft.Extensions.Options;
using TypstRender.Contracts;

namespace TypstRender.Service.Render;

/// <summary>HTTP status + payload for a render request.</summary>
public sealed record RenderOutcome(int StatusCode, byte[]? Pdf, string? Error);

/// <summary>
/// Generic Typst rendering pipeline: unpack a bundle into a temp <c>--root</c>,
/// compile the entry, return the PDF, clean up. A semaphore caps concurrent
/// compilations so the box cannot be overwhelmed; every request is logged with
/// its outcome and duration.
/// </summary>
public sealed class RenderService : IDisposable
{
    private readonly TypstRunner _typst;
    private readonly RenderOptions _options;
    private readonly ILogger<RenderService> _logger;
    private readonly SemaphoreSlim _gate;
    private readonly int _maxConcurrency;

    public RenderService(TypstRunner typst, IOptions<RenderOptions> options, ILogger<RenderService> logger)
    {
        _typst = typst;
        _options = options.Value;
        _logger = logger;
        _maxConcurrency = _options.MaxConcurrency > 0 ? _options.MaxConcurrency : Environment.ProcessorCount;
        _gate = new SemaphoreSlim(_maxConcurrency);
    }

    public async Task<RenderOutcome> RenderAsync(
        Stream body,
        string entry,
        IReadOnlyList<string> inputs,
        CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(_options.QueueTimeoutSeconds), ct))
        {
            _logger.LogWarning("Render rejected: at capacity ({Max} concurrent renders)", _maxConcurrency);
            return new RenderOutcome(StatusCodes.Status503ServiceUnavailable, null, "rendering service is at capacity");
        }

        // Everything that can throw lives inside the try so the slot is always
        // released — a failure between acquiring and the try would leak the
        // slot and, repeated, wedge the service permanently at capacity.
        var sw = Stopwatch.StartNew();
        string? workDir = null;
        try
        {
            workDir = Path.Combine(Path.GetTempPath(), "typst-render", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            // Buffer the upload only now that a slot is held, so requests queued
            // on the gate do not each pin a full copy of the body in memory.
            // ZipArchive needs a seekable stream; Kestrel enforces
            // MaxRequestBodySize (413) and the minimum data rate during this read.
            using var bundle = new MemoryStream();
            await body.CopyToAsync(bundle, ct);
            bundle.Position = 0;

            var extractError = BundleExtractor.Extract(bundle, workDir);
            if (extractError is not null)
            {
                return Log(sw, entry, new RenderOutcome(StatusCodes.Status400BadRequest, null, $"invalid bundle: {extractError}"));
            }

            var entryPath = Path.GetFullPath(Path.Combine(workDir, entry));
            if (!entryPath.StartsWith(Path.GetFullPath(workDir), StringComparison.Ordinal) || !File.Exists(entryPath))
            {
                return Log(sw, entry, new RenderOutcome(StatusCodes.Status400BadRequest, null, $"entry '{entry}' not found in bundle"));
            }

            List<string>? fontPaths = null;
            var fontsDir = Path.Combine(workDir, RenderProtocol.FontsDirectory);
            if (Directory.Exists(fontsDir))
            {
                fontPaths = [fontsDir];
            }

            var outputPath = Path.Combine(workDir, "out.pdf");
            var run = await _typst.CompileAsync(entryPath, outputPath, workDir, inputs, fontPaths, ct);

            var outcome = run switch
            {
                { StartFailed: true } => new RenderOutcome(StatusCodes.Status500InternalServerError, null, "failed to start typst"),
                { TimedOut: true } => new RenderOutcome(StatusCodes.Status504GatewayTimeout, null, $"typst timed out after {_options.TimeoutSeconds}s"),
                { Pdf: null } => new RenderOutcome(StatusCodes.Status422UnprocessableEntity, null, run.Stderr),
                _ => new RenderOutcome(StatusCodes.Status200OK, run.Pdf, null),
            };
            return Log(sw, entry, outcome);
        }
        finally
        {
            _gate.Release();
            if (workDir is not null)
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up {WorkDir}", workDir); }
            }
        }
    }

    private RenderOutcome Log(Stopwatch sw, string entry, RenderOutcome outcome)
    {
        if (outcome.StatusCode == StatusCodes.Status200OK)
        {
            _logger.LogInformation(
                "Render {Status} entry={Entry} bytes={Bytes} in {Ms}ms",
                outcome.StatusCode, entry, outcome.Pdf!.Length, sw.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation(
                "Render {Status} entry={Entry} in {Ms}ms: {Error}",
                outcome.StatusCode, entry, sw.ElapsedMilliseconds, outcome.Error);
        }
        return outcome;
    }

    public void Dispose() => _gate.Dispose();
}
