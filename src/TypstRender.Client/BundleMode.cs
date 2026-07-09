namespace TypstRender.Client;

/// <summary>Controls how the client selects template files for the uploaded bundle.</summary>
public enum BundleMode
{
    /// <summary>
    /// Scan the entry's import closure and bundle only what the render needs:
    /// the entry's directory subtree, any <c>fonts/</c> directory, plus every
    /// file reachable through string-literal imports/includes and asset reads
    /// (<c>image</c>, <c>json</c>, <c>read</c>, ...). If a dynamic
    /// <c>#import</c>/<c>#include</c> expression is found the scanner cannot be
    /// sound, so it falls back to bundling the whole template root.
    /// </summary>
    Auto = 0,

    /// <summary>Bundle every file under the template root, unconditionally.</summary>
    Full = 1,
}
