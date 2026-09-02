using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace TypstRender.Service.Render;

/// <summary>How one <c>typst compile</c> invocation ended.</summary>
public enum TypstRunStatus
{
    /// <summary>A PDF was produced. <see cref="TypstRun.Warnings"/> may still carry diagnostics.</summary>
    Succeeded,

    /// <summary>Typst rejected the document; the diagnostics are the compiler's own.</summary>
    CompileError,

    /// <summary>The process outlived its per-render timeout and was killed.</summary>
    TimedOut,

    /// <summary>The binary could not be launched at all — wrong path, not executable, wrong architecture.</summary>
    StartFailed,

    /// <summary>
    /// Typst neither produced a document nor explained itself: killed by a
    /// signal (the OOM killer), or an output file we could not read. Our fault
    /// to investigate, not the caller's document to fix.
    /// </summary>
    EngineFault,
}

/// <summary>Outcome of one <c>typst compile</c> invocation.</summary>
/// <param name="Status">How the run ended.</param>
/// <param name="Pdf">The rendered document, when <see cref="TypstRunStatus.Succeeded"/>.</param>
/// <param name="Diagnostics">Whatever typst wrote to stderr, trimmed.</param>
/// <param name="ExitCode">The process exit code, or <c>-1</c> when it never exited on its own.</param>
public sealed record TypstRun(TypstRunStatus Status, byte[]? Pdf, string Diagnostics, int ExitCode)
{
    /// <summary>
    /// Diagnostics emitted by a run that still succeeded — an unknown font
    /// family, say, which otherwise leaves a silently substituted typeface in
    /// the PDF and no trace anywhere.
    /// </summary>
    public string Warnings => Status == TypstRunStatus.Succeeded ? Diagnostics : string.Empty;
}

/// <summary>Thin wrapper around the <c>typst compile</c> CLI.</summary>
public sealed class TypstRunner(IOptions<RenderOptions> options, ILogger<TypstRunner> logger)
{
    private static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(10);

    private readonly RenderOptions _options = options.Value;

    private string? _version;

    /// <summary>
    /// The binary's self-reported version, or <c>null</c> when it cannot be run
    /// at all. Cached after the first success — the binary inside a running
    /// container does not change — so a readiness probe costs one process spawn
    /// per process lifetime, while a failure is retried on the next probe.
    /// </summary>
    public async Task<string?> TryGetVersionAsync(CancellationToken cancellationToken)
    {
        if (_version is not null)
        {
            return _version;
        }

        var psi = NewProcessStartInfo();
        psi.ArgumentList.Add("--version");

        try
        {
            using var timeoutCts = new CancellationTokenSource(VersionProbeTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);

            var reported = (await DrainAsync(stdoutTask).ConfigureAwait(false)).Trim();
            _ = await DrainAsync(stderrTask).ConfigureAwait(false);

            if (process.ExitCode != 0 || reported.Length == 0)
            {
                logger.LogError(
                    "Typst version probe: '{Binary} --version' exited {ExitCode} with output '{Output}'",
                    _options.TypstBinaryPath, process.ExitCode, reported);
                return null;
            }

            return _version = reported;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Typst version probe: cannot run '{Binary}'", _options.TypstBinaryPath);
            return null;
        }
    }

    public async Task<TypstRun> CompileAsync(
        string entryPath,
        string outputPath,
        string rootDir,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string>? fontPaths,
        CancellationToken cancellationToken)
    {
        var psi = NewProcessStartInfo();
        psi.ArgumentList.Add("compile");
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(rootDir);
        if (fontPaths is not null)
        {
            foreach (var fontPath in fontPaths)
            {
                psi.ArgumentList.Add("--font-path");
                psi.ArgumentList.Add(fontPath);
            }
        }
        if (_options.PackagePath is { Length: > 0 } packagePath)
        {
            psi.ArgumentList.Add("--package-path");
            psi.ArgumentList.Add(packagePath);
        }
        if (_options.PackageCachePath is { Length: > 0 } packageCachePath)
        {
            psi.ArgumentList.Add("--package-cache-path");
            psi.ArgumentList.Add(packageCachePath);
        }
        foreach (var input in inputs)
        {
            psi.ArgumentList.Add("--input");
            psi.ArgumentList.Add(input);
        }
        psi.ArgumentList.Add(entryPath);
        psi.ArgumentList.Add(outputPath);

        // Both cancellation sources are built before the process starts: a
        // failure between Start and the first await would leave an orphaned
        // typst running with nobody left to kill it.
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start typst process '{Binary}'", _options.TypstBinaryPath);
            return new TypstRun(TypstRunStatus.StartFailed, null, ex.Message, ExitCode: -1);
        }

        // Drain both pipes concurrently from the start so a child that fills the
        // stderr (or stdout) buffer cannot deadlock against our WaitForExit. The
        // reads complete at EOF, reached on normal exit or when we kill the tree;
        // awaiting them (rather than an event handler) gives a complete, race-free
        // stderr capture.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TryKillAsync(process).ConfigureAwait(false);
            _ = await DrainAsync(stderrTask).ConfigureAwait(false);
            _ = await DrainAsync(stdoutTask).ConfigureAwait(false);

            // The caller's own token wins: there is nobody left to answer, so let
            // the cancellation travel rather than inventing a render outcome.
            cancellationToken.ThrowIfCancellationRequested();

            return new TypstRun(TypstRunStatus.TimedOut, null, string.Empty, ExitCode: -1);
        }

        var stderr = (await DrainAsync(stderrTask).ConfigureAwait(false)).Trim();
        _ = await DrainAsync(stdoutTask).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            // A non-zero exit with nothing on stderr is not a document the caller
            // can fix — typst was killed (usually by the OOM killer).
            return stderr.Length > 0
                ? new TypstRun(TypstRunStatus.CompileError, null, stderr, process.ExitCode)
                : new TypstRun(
                    TypstRunStatus.EngineFault,
                    null,
                    $"typst exited with code {process.ExitCode} without a diagnostic",
                    process.ExitCode);
        }

        try
        {
            var pdf = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            return new TypstRun(TypstRunStatus.Succeeded, pdf, stderr, process.ExitCode);
        }
        catch (FileNotFoundException)
        {
            logger.LogError("typst exited 0 but produced no output at {OutputPath}", outputPath);
            return new TypstRun(
                TypstRunStatus.EngineFault, null, "typst reported success but produced no output", process.ExitCode);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Could not read the rendered document at {OutputPath}", outputPath);
            return new TypstRun(
                TypstRunStatus.EngineFault,
                null,
                $"could not read the rendered document ({ex.GetType().Name})",
                process.ExitCode);
        }
    }

    private ProcessStartInfo NewProcessStartInfo() => new()
    {
        FileName = _options.TypstBinaryPath,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    // Kill returns before the child is reaped, so wait briefly for it to go:
    // the caller deletes the work directory next, and a process still writing
    // into it turns cleanup into a leaked directory.
    private static async Task TryKillAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch
        {
            /* best-effort */
        }
    }

    // Awaits a pipe read to completion, tolerating a pipe closed by a kill or a
    // cancelled read so stderr/stdout capture stays best-effort.
    private static async Task<string> DrainAsync(Task<string> readTask)
    {
        try { return await readTask.ConfigureAwait(false); }
        catch { return string.Empty; }
    }
}
