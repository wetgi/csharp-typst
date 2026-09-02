namespace TypstRender.Contracts;

/// <summary>
/// Wire-protocol constants shared by the rendering shim and its clients.
/// A render request is a zipped Typst project (the <c>--root</c> tree) sent as
/// the raw POST body; the entry file and typst inputs travel as query params.
/// </summary>
public static class RenderProtocol
{
    /// <summary>Route that compiles a bundle: <c>POST /render</c> with a zip body.</summary>
    public const string RenderPath = "/render";

    /// <summary>Liveness route.</summary>
    public const string HealthPath = "/health";

    /// <summary>Content type of the request body.</summary>
    public const string BundleContentType = "application/zip";

    /// <summary>Query parameter naming the entry <c>.typ</c> file inside the bundle.</summary>
    public const string EntryQueryParam = "entry";

    /// <summary>
    /// Repeatable query parameter carrying a <c>--input</c> pair as <c>key=value</c>,
    /// passed through to <c>typst compile --input key=value</c>.
    /// </summary>
    public const string InputQueryParam = "input";

    /// <summary>Default entry template name when none is supplied.</summary>
    public const string DefaultEntry = "main.typ";

    /// <summary>
    /// Conventional file name the client writes the serialized data object to,
    /// placed at the bundle root.
    /// </summary>
    public const string DataFileName = "data.json";

    /// <summary>
    /// Conventional <c>--input</c> key pointing at <see cref="DataFileName"/> as a
    /// project-root-relative path (e.g. <c>/data.json</c>). Templates read it via
    /// <c>sys.inputs.at("data-path")</c>.
    /// </summary>
    public const string DataPathInputKey = "data-path";

    /// <summary>Optional bundle sub-directory whose fonts are exposed via <c>--font-path</c>.</summary>
    public const string FontsDirectory = "fonts";
}
