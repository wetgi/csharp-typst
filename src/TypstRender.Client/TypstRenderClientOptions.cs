namespace TypstRender.Client;

/// <summary>Configuration for <see cref="TypstRenderClient"/>.</summary>
public sealed class TypstRenderClientOptions
{
    /// <summary>Base address of the rendering service, e.g. <c>http://typst-render:8080</c>.</summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>Overall request timeout. Defaults to 60 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Default folder shipped as the Typst <c>--root</c>; render entries are
    /// addressed relative to it (e.g. <c>invoice/main.typ</c>). Required for the
    /// entry-based <c>RenderAsync</c> overload unless the request supplies its own
    /// <see cref="TypstRenderRequest.TemplateRoot"/> or <see cref="TypstRenderRequest.Files"/>.
    /// </summary>
    public string? TemplateRoot { get; set; }

    /// <summary>
    /// Default bundling strategy. <see cref="BundleMode.Auto"/> ships only the
    /// entry's import closure; <see cref="BundleMode.Full"/> ships the whole root.
    /// </summary>
    public BundleMode BundleMode { get; set; } = BundleMode.Auto;
}
