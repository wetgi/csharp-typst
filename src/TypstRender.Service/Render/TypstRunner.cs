using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace TypstRender.Service.Render;

/// <summary>Outcome of one <c>typst compile</c> invocation.</summary>
public sealed record TypstRun(byte[]? Pdf, string Stderr, bool TimedOut, bool StartFailed);

/// <summary>Thin wrapper around the <c>typst compile</c> CLI.</summary>
public sealed class TypstRunner(IOptions<RenderOptions> options, ILogger<TypstRunner> logger)
{
    private readonly RenderOptions _options = options.Value;

    public async Task<TypstRun> CompileAsync(
        string entryPath,
        string outputPath,
        string rootDir,
        IReadOnlyList<string> inputs,
        IReadOnlyList<string>? fontPaths,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.TypstBinaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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
        foreach (var input in inputs)
        {
            psi.ArgumentList.Add("--input");
            psi.ArgumentList.Add(input);
        }
        psi.ArgumentList.Add(entryPath);
        psi.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start typst process '{Binary}'", _options.TypstBinaryPath);
            return new TypstRun(null, ex.Message, TimedOut: false, StartFailed: true);
        }

        // Drain both pipes concurrently from the start so a child that fills the
        // stderr (or stdout) buffer cannot deadlock against our WaitForExit. The
        // reads complete at EOF, reached on normal exit or when we kill the tree;
        // awaiting them (rather than an event handler) gives a complete, race-free
        // stderr capture.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            // A timeout cancels timeoutCts; a shutdown/abort cancels the caller's ct.
            var partialStderr = await DrainAsync(stderrTask);
            return new TypstRun(null, partialStderr.Trim(), TimedOut: !ct.IsCancellationRequested, StartFailed: false);
        }

        var stderr = (await DrainAsync(stderrTask)).Trim();
        _ = await DrainAsync(stdoutTask); // observe so its task never faults unobserved

        if (process.ExitCode != 0)
        {
            return new TypstRun(null, stderr, TimedOut: false, StartFailed: false);
        }

        try
        {
            var pdf = await File.ReadAllBytesAsync(outputPath, ct);
            return new TypstRun(pdf, string.Empty, TimedOut: false, StartFailed: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "typst exited 0 but no output at {OutputPath}", outputPath);
            return new TypstRun(null, "typst reported success but produced no output", TimedOut: false, StartFailed: false);
        }
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    // Awaits a pipe read to completion, tolerating a pipe closed by a kill or a
    // cancelled read so stderr/stdout capture stays best-effort.
    private static async Task<string> DrainAsync(Task<string> readTask)
    {
        try { return await readTask; }
        catch { return string.Empty; }
    }
}
