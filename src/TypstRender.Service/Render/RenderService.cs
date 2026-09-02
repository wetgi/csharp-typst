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
    /// <summary>
    /// Parent of every per-render work directory. Shared with
    /// <see cref="TempWorkspaceCleaner"/>, which sweeps what a crashed process
    /// could not delete itself.
    /// </summary>
    public static readonly string WorkRoot = Path.Combine(Path.GetTempPath(), "typst-render");

    // Enough to carry a Typst diagnostic with its source excerpt, small enough
    // that a pathological template cannot make the error body the payload.
    private const int MaxErrorLength = 8 * 1024;

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
        _maxConcurrency = _options.EffectiveMaxConcurrency;
        _gate = new SemaphoreSlim(_maxConcurrency);

        _logger.LogInformation(
            "Render limits: concurrency={Concurrency} timeout={Timeout}s queueTimeout={QueueTimeout}s "
                + "maxUpload={MaxUpload}B maxExtracted={MaxExtracted}B maxEntries={MaxEntries}",
            _maxConcurrency, _options.TimeoutSeconds, _options.QueueTimeoutSeconds,
            _options.MaxUploadBytes, _options.MaxExtractedBytes, _options.MaxBundleEntries);
    }

    public async Task<RenderOutcome> RenderAsync(
        Stream body,
        string entry,
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(_options.QueueTimeoutSeconds), cancellationToken)
            .ConfigureAwait(false))
        {
            _logger.LogWarning("Render rejected: at capacity ({Max} concurrent renders)", _maxConcurrency);
            return new RenderOutcome(
                StatusCodes.Status503ServiceUnavailable, null, "rendering service is at capacity");
        }

        // Everything that can throw lives inside the try so the slot is always
        // released — a failure between acquiring and the try would leak the
        // slot and, repeated, wedge the service permanently at capacity.
        var sw = Stopwatch.StartNew();
        string? workDir = null;
        try
        {
            workDir = Path.Combine(WorkRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            // Buffer the upload only now that a slot is held, so requests queued
            // on the gate do not each pin a full copy of the body in memory.
            // ZipArchive needs a seekable stream; Kestrel enforces
            // MaxRequestBodySize (413) and the minimum data rate during this read.
            using var bundle = new MemoryStream();
            await body.CopyToAsync(bundle, cancellationToken).ConfigureAwait(false);
            bundle.Position = 0;

            var extractError = await BundleExtractor
                .ExtractAsync(bundle, workDir, ExtractionLimits, cancellationToken)
                .ConfigureAwait(false);
            if (extractError is not null)
            {
                return Log(sw, entry, new RenderOutcome(
                    StatusCodes.Status400BadRequest, null, $"invalid bundle: {extractError}"));
            }

            var entryPath = ResolveEntryPath(workDir, entry);
            if (entryPath is null)
            {
                return Log(sw, entry, new RenderOutcome(
                    StatusCodes.Status400BadRequest, null, $"entry '{entry}' not found in bundle"));
            }

            List<string>? fontPaths = null;
            var fontsDir = Path.Combine(workDir, RenderProtocol.FontsDirectory);
            if (Directory.Exists(fontsDir))
            {
                fontPaths = [fontsDir];
            }

            var outputPath = Path.Combine(workDir, "out.pdf");
            var run = await _typst
                .CompileAsync(entryPath, outputPath, workDir, inputs, fontPaths, cancellationToken)
                .ConfigureAwait(false);

            if (run.Warnings.Length > 0)
            {
                // A warning still renders, so it never reaches the caller — but a
                // substituted font is exactly the kind of silent difference an
                // operator needs to be able to find afterwards.
                _logger.LogWarning(
                    "Typst warnings entry={Entry}: {Warnings}", entry, Sanitize(run.Warnings, workDir));
            }

            var outcome = run.Status switch
            {
                TypstRunStatus.Succeeded => new RenderOutcome(StatusCodes.Status200OK, run.Pdf, null),
                TypstRunStatus.CompileError => new RenderOutcome(
                    StatusCodes.Status422UnprocessableEntity, null, Sanitize(run.Diagnostics, workDir)),
                TypstRunStatus.TimedOut => new RenderOutcome(
                    StatusCodes.Status504GatewayTimeout, null, $"typst timed out after {_options.TimeoutSeconds}s"),
                TypstRunStatus.StartFailed => new RenderOutcome(
                    StatusCodes.Status500InternalServerError,
                    null,
                    $"failed to start typst ('{_options.TypstBinaryPath}')"),
                _ => new RenderOutcome(
                    StatusCodes.Status500InternalServerError, null, Sanitize(run.Diagnostics, workDir)),
            };
            return Log(sw, entry, outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up, or the host is shutting down. Nobody is left to
            // read a response, and reporting it as a render failure would corrupt
            // the very rates an operator alerts on.
            _logger.LogInformation(
                "Render cancelled entry={Entry} after {Ms}ms", entry, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            // Guarded: a Release racing host teardown would otherwise throw from
            // inside this finally and mask the original failure.
            try { _gate.Release(); }
            catch (ObjectDisposedException) { /* the host is tearing down */ }

            if (workDir is not null)
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Failed to clean up {WorkDir}", workDir); }
            }
        }
    }

    private BundleExtractor.Limits ExtractionLimits =>
        new(_options.MaxBundleEntries, _options.MaxExtractedBytes);

    /// <summary>
    /// Resolves the requested entry inside the unpacked bundle, or <c>null</c>
    /// when it does not name a file within it. The prefix test carries a
    /// trailing separator so a sibling directory whose name merely starts with
    /// the work directory's cannot satisfy it.
    /// </summary>
    private static string? ResolveEntryPath(string workDir, string entry)
    {
        var root = Path.GetFullPath(workDir) + Path.DirectorySeparatorChar;

        string entryPath;
        try
        {
            entryPath = Path.GetFullPath(Path.Combine(workDir, entry));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null; // a name the filesystem cannot express is not in the bundle
        }

        return entryPath.StartsWith(root, StringComparison.Ordinal) && File.Exists(entryPath)
            ? entryPath
            : null;
    }

    /// <summary>
    /// Prepares compiler output for a response body: the per-render temp path is
    /// an internal detail that Typst happily prints, and the output itself is
    /// attacker-influenced, so it is both redacted and bounded.
    /// </summary>
    private static string Sanitize(string diagnostics, string workDir)
    {
        var text = diagnostics
            .Replace(Path.GetFullPath(workDir), "<root>", StringComparison.Ordinal)
            .Replace(workDir, "<root>", StringComparison.Ordinal);

        return text.Length <= MaxErrorLength
            ? text
            : text.Substring(0, MaxErrorLength)
                + $"\n… truncated, {text.Length - MaxErrorLength} more characters";
    }

    private RenderOutcome Log(Stopwatch sw, string entry, RenderOutcome outcome)
    {
        if (outcome.StatusCode == StatusCodes.Status200OK)
        {
            _logger.LogInformation(
                "Render {Status} entry={Entry} bytes={Bytes} in {Ms}ms",
                outcome.StatusCode, entry, outcome.Pdf!.Length, sw.ElapsedMilliseconds);
            return outcome;
        }

        // Severity by status class, so alerting on Warning+ surfaces exactly the
        // outcomes that need a human: 5xx is ours to fix, capacity/timeout is a
        // sizing signal, and a rejected bundle is the caller's to correct.
        var level = outcome.StatusCode switch
        {
            StatusCodes.Status503ServiceUnavailable or StatusCodes.Status504GatewayTimeout => LogLevel.Warning,
            >= 500 => LogLevel.Error,
            _ => LogLevel.Information,
        };

        _logger.Log(
            level,
            "Render {Status} entry={Entry} in {Ms}ms: {Error}",
            outcome.StatusCode, entry, sw.ElapsedMilliseconds, outcome.Error);
        return outcome;
    }

    public void Dispose() => _gate.Dispose();
}
