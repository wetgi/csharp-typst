using TypstRender.Contracts;

namespace TypstRender.Client;

/// <summary>
/// Full-control render request. The common case does not need this type — use
/// <see cref="ITypstRenderClient.RenderAsync(string, object?, System.Threading.CancellationToken)"/>
/// with a configured <see cref="TypstRenderClientOptions.TemplateRoot"/> instead.
/// </summary>
public sealed class TypstRenderRequest
{
    /// <summary>
    /// Entry <c>.typ</c> file, relative to the template root (e.g. <c>invoice/main.typ</c>).
    /// </summary>
    public string Entry { get; set; } = RenderProtocol.DefaultEntry;

    /// <summary>
    /// Folder shipped as the Typst <c>--root</c>. Overrides
    /// <see cref="TypstRenderClientOptions.TemplateRoot"/>. Ignored when
    /// <see cref="Files"/> is set.
    /// </summary>
    public string? TemplateRoot { get; set; }

    /// <summary>
    /// Object serialized to <c>data.json</c> at the bundle root and exposed to the
    /// template via the <c>data-path</c> input. <c>null</c> sends no data file.
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    /// Extra <c>typst --input key=value</c> pairs, in addition to the conventional
    /// <c>data-path</c> input the client sets when <see cref="Data"/> is non-null.
    /// </summary>
    public IDictionary<string, string> Inputs { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// How template files are selected for the bundle. Defaults to
    /// <see cref="TypstRenderClientOptions.BundleMode"/> when unset.
    /// </summary>
    public BundleMode? BundleMode { get; set; }

    /// <summary>
    /// Extra in-memory files (root-relative path =&gt; bytes) overlaid on the
    /// bundle alongside the template — for content generated at render time,
    /// such as charts, barcodes or signatures. Templates reference them like
    /// any other asset (e.g. <c>image("generated/chart.svg")</c>); the bundle
    /// scanner treats declared paths as present even when they do not exist on
    /// disk. When a path matches a file already in the bundle — whether scanned
    /// from disk or supplied via an in-memory <see cref="Files"/> set — the
    /// render-time content here wins, so a template can keep a local-preview
    /// placeholder that this replaces. Paths must not collide with <c>data.json</c>.
    /// </summary>
    public IDictionary<string, byte[]> ExtraFiles { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>
    /// In-memory bundle (root-relative path =&gt; bytes) for callers that do not keep
    /// templates on disk (embedded resources, generated templates). When set, the
    /// files are shipped as-is and no folder scanning happens.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]>? Files { get; set; }
}
