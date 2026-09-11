namespace TypstRender.Service.Render;

/// <summary>Limits for the rendering service, bound from the <c>Render</c> config section.</summary>
public sealed class RenderOptions
{
    public const string SectionName = "Render";

    /// <summary>The typst executable; defaults to <c>typst</c> resolved via <c>PATH</c>.</summary>
    public string TypstBinaryPath { get; set; } = "typst";

    /// <summary>Per-render hard timeout before the process tree is killed.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum accepted request body (the uploaded zip). Default 20 MB.</summary>
    public long MaxUploadBytes { get; set; } = 20L * 1024 * 1024;

    /// <summary>
    /// Maximum total size of the unpacked bundle. Default 200 MB.
    /// <see cref="MaxUploadBytes"/> only caps the <em>compressed</em> upload, so
    /// without this a well-formed 20 MB zip of highly compressible data unpacks
    /// to tens of gigabytes while holding a concurrency slot.
    /// </summary>
    public long MaxExtractedBytes { get; set; } = 200L * 1024 * 1024;

    /// <summary>
    /// Maximum number of records in an uploaded zip. Default 2000 — a template
    /// closure is tens of entries; anything near this is a malformed or hostile
    /// bundle, not a document.
    /// </summary>
    public int MaxBundleEntries { get; set; } = 2000;

    /// <summary>Maximum concurrent typst compilations. <c>0</c> means the CPU count.</summary>
    public int MaxConcurrency { get; set; }

    /// <summary>How long a request waits for a free slot before returning 503.</summary>
    public int QueueTimeoutSeconds { get; set; } = 10;

    /// <summary>Graceful-shutdown drain window for in-flight renders.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 30;
}
