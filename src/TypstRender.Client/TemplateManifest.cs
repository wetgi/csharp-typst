namespace TypstRender.Client;

/// <summary>
/// The set of files the client would upload for a given entry, as computed by
/// the bundle scanner — produced without making any HTTP request. Handy for
/// debugging "why is this asset missing from the render?" or "why is my bundle
/// the whole template folder?" before paying for a round-trip.
/// </summary>
public sealed class TemplateManifest
{
    internal TemplateManifest(IReadOnlyList<string> files, string? fullFolderReason)
    {
        Files = files;
        FullFolderReason = fullFolderReason;
    }

    /// <summary>Root-relative, '/'-separated paths that would be bundled, sorted ordinally.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// Non-null when the scanner widened the bundle to the whole template root
    /// (for example a dynamic <c>#import</c> it could not resolve statically),
    /// explaining why. See <see cref="BundleMode.Auto"/>.
    /// </summary>
    public string? FullFolderReason { get; }

    /// <summary>True when the bundle was widened to the whole template root.</summary>
    public bool IsFullFolder => FullFolderReason is not null;
}
