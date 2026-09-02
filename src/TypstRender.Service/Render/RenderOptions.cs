using System.ComponentModel.DataAnnotations;

namespace TypstRender.Service.Render;

/// <summary>
/// Limits for the rendering service, bound from the <c>Render</c> config section
/// and validated at startup — a nonsensical value fails the host with a message
/// naming the key, rather than surfacing as a puzzling runtime behaviour.
/// </summary>
public sealed class RenderOptions
{
    public const string SectionName = "Render";

    /// <summary>The typst executable; defaults to <c>typst</c> resolved via <c>PATH</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string TypstBinaryPath { get; set; } = "typst";

    /// <summary>Per-render hard timeout before the process tree is killed.</summary>
    [Range(1, 3600)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum accepted request body (the uploaded zip). Default 20 MB.</summary>
    [Range(1024, long.MaxValue)]
    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// Maximum total size of the unpacked bundle. Default 200 MB.
    /// <see cref="MaxUploadBytes"/> only caps the <em>compressed</em> upload, so
    /// without this a well-formed 20 MB zip of highly compressible data could
    /// fill the disk on extraction.
    /// </summary>
    [Range(1024, long.MaxValue)]
    public long MaxExtractedBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>
    /// Maximum number of files in an uploaded bundle. Default 2000 — a template
    /// closure is tens of files; anything near this is a malformed or hostile
    /// bundle, not a document.
    /// </summary>
    [Range(1, 1_000_000)]
    public int MaxBundleEntries { get; set; } = 2000;

    /// <summary>Maximum concurrent typst compilations. <c>0</c> means the CPU count.</summary>
    [Range(0, 1024)]
    public int MaxConcurrency { get; set; }

    /// <summary>How long a request waits for a free slot before returning 503.</summary>
    [Range(0, 3600)]
    public int QueueTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Graceful-shutdown drain window for in-flight renders. Kept above
    /// <see cref="TimeoutSeconds"/> on purpose: an equal window means a render
    /// that started a second before SIGTERM is always aborted mid-flight.
    /// </summary>
    [Range(0, 3600)]
    public int ShutdownTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Directory of pre-seeded Typst packages, passed as <c>--package-path</c>.
    /// Set this (and block egress) for an air-gapped deployment: a template that
    /// imports <c>@preview/...</c> otherwise downloads and executes third-party
    /// code from packages.typst.org at render time. Unset by default, which
    /// keeps Typst's own resolution behaviour.
    /// </summary>
    public string? PackagePath { get; set; }

    /// <summary>
    /// Writable cache for downloaded Typst packages, passed as
    /// <c>--package-cache-path</c>. Worth pointing at a mounted volume: the
    /// default lives under <c>$HOME</c>, so in a container every restart
    /// re-downloads, and a read-only root filesystem fails outright.
    /// </summary>
    public string? PackageCachePath { get; set; }

    /// <summary>
    /// The effective concurrency limit: <see cref="MaxConcurrency"/> when set,
    /// otherwise the CPU count. Named so the <c>0 = auto</c> convention lives in
    /// one place instead of being re-derived by every caller.
    /// </summary>
    public int EffectiveMaxConcurrency => MaxConcurrency > 0 ? MaxConcurrency : Environment.ProcessorCount;
}
