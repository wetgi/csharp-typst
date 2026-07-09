using System.Text.RegularExpressions;
using TypstRender.Contracts;

namespace TypstRender.Client;

/// <summary>
/// Computes which files under a template root belong in the uploaded bundle.
///
/// In <see cref="BundleMode.Auto"/> the result is the entry's directory subtree
/// plus the conventional <c>fonts/</c> directory, extended with everything
/// reachable through string-literal references (<c>#import</c>/<c>#include</c>
/// and asset readers like <c>image(...)</c>) — so shared modules outside the
/// entry's folder ride along while sibling templates stay home. The scanner is
/// deliberately fail-safe: a dynamic <c>#import</c>/<c>#include</c> expression
/// it cannot resolve statically widens the bundle to the whole root instead of
/// risking a missing file. Asset readers with non-literal arguments (the
/// <c>json(data-path)</c> data convention) are tolerated because the entry's
/// subtree ships in full anyway.
/// </summary>
internal static class TemplateScanner
{
    // #import "..." / #include "..." (also valid without the leading # in code mode).
    private static readonly Regex LiteralImport = new(
        "(?<![\\w-])(?:import|include)\\s+\"([^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Asset readers taking a string-literal path as their first argument.
    private static readonly Regex LiteralReader = new(
        "(?<![\\w.-])(?:image|read|json|csv|yaml|toml|xml|cbor|bibliography)\\s*\\(\\s*\"([^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // #import/#include followed by anything but a string literal: a path we
    // cannot resolve statically.
    private static readonly Regex DynamicImport = new(
        "#(?:import|include)\\s+(?!\")\\S",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BlockComments = new(
        @"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    // Line comments; "(?<!:)" keeps URLs like http:// inside strings intact.
    private static readonly Regex LineComments = new(
        @"(?<!:)//[^\r\n]*", RegexOptions.Compiled);

    /// <summary>
    /// Scans the template under <paramref name="root"/> starting at
    /// <paramref name="entry"/> (root-relative). Throws
    /// <see cref="FileNotFoundException"/> when the entry or a string-literal
    /// reference does not exist on disk. Paths in <paramref name="extraPaths"/>
    /// (normalized root-relative) are files the client injects at render time
    /// (like <c>data.json</c>): references to them are tolerated and they are
    /// never expected on disk.
    /// </summary>
    public static TemplateScanResult Scan(
        string root, string entry, BundleMode mode, IReadOnlyCollection<string>? extraPaths = null)
    {
        var extras = extraPaths is { Count: > 0 }
            ? new HashSet<string>(extraPaths, StringComparer.Ordinal)
            : null;

        root = Path.GetFullPath(root);
        var entryRel = NormalizeSlashes(entry).TrimStart('/');
        if (!File.Exists(ToFullPath(root, entryRel)))
        {
            throw new FileNotFoundException(
                $"Entry '{entry}' not found under template root '{root}'.", entryRel);
        }

        if (mode == BundleMode.Full)
        {
            return new TemplateScanResult(AllFiles(root), null);
        }

        var files = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Include(string rel)
        {
            files.Add(rel);
            if (rel.EndsWith(".typ", StringComparison.OrdinalIgnoreCase) && visited.Add(rel))
            {
                pending.Enqueue(rel);
            }
        }

        var entryDirRel = ParentDir(entryRel);
        IncludeSubtree(root, entryDirRel, Include);
        if (Directory.Exists(Path.Combine(root, RenderProtocol.FontsDirectory)))
        {
            IncludeSubtree(root, RenderProtocol.FontsDirectory, Include);
        }

        while (pending.Count > 0)
        {
            var rel = pending.Dequeue();
            var text = File.ReadAllText(ToFullPath(root, rel));
            text = LineComments.Replace(BlockComments.Replace(text, " "), " ");

            var dynamic = DynamicImport.Match(text);
            if (dynamic.Success)
            {
                // Cannot prove what the expression resolves to: ship everything.
                return new TemplateScanResult(
                    AllFiles(root),
                    $"dynamic import expression '{dynamic.Value}' in '{rel}'");
            }

            foreach (var reference in CollectReferences(text))
            {
                if (reference.StartsWith("@", StringComparison.Ordinal))
                {
                    continue; // package import, resolved by the service-side typst
                }

                var resolved = ResolveAgainstRoot(ParentDir(rel), reference);
                if (resolved is null)
                {
                    throw new InvalidOperationException(
                        $"Path '{reference}' referenced from '{rel}' escapes the template root '{root}'.");
                }

                if (string.Equals(resolved, RenderProtocol.DataFileName, StringComparison.Ordinal)
                    || extras?.Contains(resolved) == true)
                {
                    continue; // injected by the client at render time
                }

                if (!File.Exists(ToFullPath(root, resolved)))
                {
                    throw new FileNotFoundException(
                        $"'{reference}' referenced from '{rel}' resolves to '{resolved}', " +
                        $"which does not exist under template root '{root}'.", resolved);
                }

                Include(resolved);
            }
        }

        return new TemplateScanResult([.. files], null);
    }

    private static IEnumerable<string> CollectReferences(string text)
    {
        foreach (Match m in LiteralImport.Matches(text))
        {
            yield return m.Groups[1].Value;
        }

        foreach (Match m in LiteralReader.Matches(text))
        {
            yield return m.Groups[1].Value;
        }
    }

    private static void IncludeSubtree(string root, string dirRel, Action<string> include)
    {
        var dir = dirRel.Length == 0 ? root : ToFullPath(root, dirRel);
        foreach (var rel in EnumerateRelative(root, dir))
        {
            include(rel);
        }
    }

    private static List<string> AllFiles(string root)
    {
        var all = new List<string>(EnumerateRelative(root, root));
        all.Sort(StringComparer.Ordinal);
        return all;
    }

    private static IEnumerable<string> EnumerateRelative(string root, string dir)
    {
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = path.Substring(root.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            yield return NormalizeSlashes(rel);
        }
    }

    /// <summary>
    /// Resolves a Typst path (absolute = root-relative, otherwise relative to the
    /// referencing file's directory) to a normalized root-relative path, or
    /// <c>null</c> when it walks above the root.
    /// </summary>
    private static string? ResolveAgainstRoot(string baseDirRel, string reference)
    {
        reference = NormalizeSlashes(reference);
        var combined = reference.StartsWith("/", StringComparison.Ordinal)
            ? reference.TrimStart('/')
            : baseDirRel.Length == 0 ? reference : baseDirRel + "/" + reference;

        var parts = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(segment);
            }
        }

        return string.Join("/", parts);
    }

    private static string ParentDir(string rel)
    {
        var idx = rel.LastIndexOf('/');
        return idx < 0 ? string.Empty : rel.Substring(0, idx);
    }

    private static string ToFullPath(string root, string rel)
        => Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeSlashes(string path) => path.Replace('\\', '/');
}

/// <summary>Outcome of a <see cref="TemplateScanner.Scan"/>.</summary>
internal sealed class TemplateScanResult(IReadOnlyList<string> files, string? fullFolderReason)
{
    /// <summary>Root-relative, '/'-separated paths to bundle, sorted ordinally.</summary>
    public IReadOnlyList<string> Files { get; } = files;

    /// <summary>Non-null when the scanner widened the bundle to the whole root, and why.</summary>
    public string? FullFolderReason { get; } = fullFolderReason;
}
