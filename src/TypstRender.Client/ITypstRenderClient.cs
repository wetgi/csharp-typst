namespace TypstRender.Client;

/// <summary>Client for the Typst rendering service.</summary>
public interface ITypstRenderClient
{
    /// <summary>
    /// Renders the template addressed by <paramref name="entry"/> (relative to the
    /// configured <see cref="TypstRenderClientOptions.TemplateRoot"/>, e.g.
    /// <c>invoice/main.typ</c>). The client scans the entry's import closure,
    /// bundles the required files, serializes <paramref name="data"/> to
    /// <c>data.json</c> at the bundle root and exposes it via the <c>data-path</c>
    /// input (templates read it with <c>sys.inputs.at("data-path")</c>).
    /// </summary>
    /// <param name="entry">Entry <c>.typ</c> file, relative to the template root.</param>
    /// <param name="data">Object serialized to <c>data.json</c>; <c>null</c> sends no data file.</param>
    /// <param name="cancellationToken">Token used to cancel the request.</param>
    /// <returns>The rendered PDF bytes.</returns>
    Task<byte[]> RenderAsync(
        string entry,
        object? data = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders with full control over root, bundling, extra inputs, or an in-memory
    /// file set. See <see cref="TypstRenderRequest"/>.
    /// </summary>
    Task<byte[]> RenderAsync(
        TypstRenderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming counterpart of <see cref="RenderAsync(string, object?, CancellationToken)"/>:
    /// returns the PDF as a <see cref="Stream"/> the caller reads and disposes,
    /// instead of buffering the whole document into a <c>byte[]</c>. Dispose the
    /// returned stream to release the underlying HTTP response.
    /// </summary>
    Task<Stream> RenderToStreamAsync(
        string entry,
        object? data = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streaming counterpart of <see cref="RenderAsync(TypstRenderRequest, CancellationToken)"/>.
    /// Dispose the returned stream to release the underlying HTTP response.
    /// </summary>
    Task<Stream> RenderToStreamAsync(
        TypstRenderRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the templates discoverable under the configured
    /// <see cref="TypstRenderClientOptions.TemplateRoot"/>: every directory, at any
    /// depth, that contains a <c>main.typ</c> entry. Directories without an entry
    /// (shared modules, <c>fonts/</c>) are skipped. Names are root-relative,
    /// <c>'/'</c>-separated paths (e.g. <c>invoice</c>, <c>invoice/paid</c>),
    /// sorted ordinally. Returns an empty list when the root does not exist on
    /// disk; throws <see cref="System.InvalidOperationException"/> when no root is
    /// configured.
    /// </summary>
    /// <remarks>
    /// Each returned name addresses a template whose entry is <c>main.typ</c>; pass
    /// <c>name + "/main.typ"</c> to <see cref="RenderAsync(string, object?, CancellationToken)"/>.
    /// Templates with a differently named entry are not discovered here but can
    /// still be rendered by their explicit entry path.
    /// </remarks>
    IReadOnlyList<string> GetTemplates();

    /// <summary>
    /// Computes which files the client would upload for <paramref name="entry"/>
    /// (relative to the configured <see cref="TypstRenderClientOptions.TemplateRoot"/>)
    /// without making any request — the entry's import closure, or the whole root
    /// when the scanner had to widen (see <see cref="TemplateManifest.FullFolderReason"/>).
    /// A reference that does not exist on disk fails here with the chain of files
    /// that led to it, rather than as an opaque server error after a round-trip.
    /// Throws <see cref="System.ArgumentException"/> when <paramref name="entry"/>
    /// is blank, <see cref="System.InvalidOperationException"/> when no template
    /// root is configured, and <see cref="System.IO.DirectoryNotFoundException"/>
    /// when the configured root does not exist on disk — note this differs from
    /// <see cref="GetTemplates"/>, which returns an empty list for a missing root.
    /// </summary>
    /// <param name="entry">Entry <c>.typ</c> file, relative to the template root.</param>
    /// <param name="bundleMode">Overrides <see cref="TypstRenderClientOptions.BundleMode"/> when set.</param>
    /// <param name="extraFiles">
    /// Root-relative paths the render request would inject via
    /// <see cref="TypstRenderRequest.ExtraFiles"/>; references to them are
    /// tolerated when they do not exist on disk. If a path matches a bundled
    /// template file, render-time content replaces that file when the request is
    /// sent. Missing extra files are not listed in the returned manifest — like
    /// <c>data.json</c>, they are per-request additions.
    /// </param>
    TemplateManifest GetBundleManifest(
        string entry, BundleMode? bundleMode = null, IEnumerable<string>? extraFiles = null);
}
