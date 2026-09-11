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
/// entry's folder ride along while sibling templates stay home.
///
/// Before matching, comments and raw blocks are masked while strings and line
/// breaks are preserved. Imports are matched only in code position, so prose
/// such as <c>Prices include "VAT"</c> is not mistaken for an import.
///
/// The scanner stays deliberately fail-safe: an <c>#import</c>/<c>#include</c>
/// path it cannot resolve statically (a variable, or a string it concatenates)
/// widens the bundle to the whole root rather than risk a missing file. Asset
/// readers with non-literal arguments (the <c>json(data-path)</c> data
/// convention) are tolerated instead, because a reader cannot pull in further
/// references of its own.
///
/// Reader calls remain deliberately conservative: only a literal first
/// argument is followed. Use <see cref="BundleMode.Full"/> when paths are
/// assembled dynamically or document text resembles a reader call.
/// </summary>
internal static class TemplateScanner
{
    // Code position: an import/include only counts when it follows '#', a brace,
    // a semicolon, or begins a line. Typst allows a bare `import` in code mode,
    // so requiring '#' would miss those; allowing it anywhere matches prose.
    private const string CodePosition = @"(?:^|[#{};])[ \t]*";

    // A literal that is immediately concatenated is only a prefix, not a path.
    private const string NotConcatenated = @"(?!\s*\+)";

    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline;

    // #import "..." / #include "..." (also valid without the leading # in code mode).
    private static readonly Regex LiteralImport = new(
        CodePosition + @"(?:import|include)\s+""([^""\r\n]+)""" + NotConcatenated, Options);

    // Asset readers taking a string-literal path as their first argument.
    private static readonly Regex LiteralReader = new(
        @"(?<![\w.\-])(?:image|read|json|csv|yaml|toml|xml|cbor|bibliography)\s*\(\s*""([^""\r\n]+)"""
            + NotConcatenated,
        Options);

    // #import/#include followed by an expression rather than a string literal.
    private static readonly Regex DynamicImport = new(
        CodePosition + @"(?:import|include)\s+(?![""\r\n])\S", Options);

    // #import/#include whose path is a literal glued to an expression — as
    // unresolvable as a bare variable, and previously mistaken for a real path.
    private static readonly Regex ConcatenatedImport = new(
        CodePosition + @"(?:import|include)\s+""[^""\r\n]*""\s*\+", Options);

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
        var entryRel = NormalizeEntry(entry);
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
            var source = PrepareSource(File.ReadAllText(ToFullPath(root, rel)));

            var unresolvable = FirstUnresolvableImport(source);
            if (unresolvable is not null)
            {
                // Cannot prove what the expression resolves to: ship everything.
                return new TemplateScanResult(
                    AllFiles(root), $"unresolvable import expression '{unresolvable}' in '{rel}'");
            }

            foreach (var reference in CollectReferences(source))
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

                if (resolved.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Path '{reference}' referenced from '{rel}' does not name a file.");
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

    /// <summary>
    /// Normalizes a root-relative entry path and rejects rooted paths or paths
    /// that try to climb out of the root.
    /// </summary>
    public static string NormalizeEntry(string? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry))
        {
            throw new ArgumentException("Entry must name a .typ file inside the template root.", nameof(entry));
        }

        var rel = NormalizeSlashes(entry);
        if (rel.StartsWith("/", StringComparison.Ordinal) || IsDriveQualified(rel))
        {
            throw new ArgumentException(
                $"Entry '{entry}' must be relative to the template root.", nameof(entry));
        }

        foreach (var segment in rel.Split('/'))
        {
            if (segment == "..")
            {
                throw new ArgumentException(
                    $"Entry '{entry}' must be a path inside the template root, without '..'.", nameof(entry));
            }
        }

        return rel;
    }

    /// <summary>
    /// Masks comments and raw blocks without changing source positions or line
    /// breaks. Strings are skipped intact so comment delimiters inside them do
    /// not hide later imports.
    /// </summary>
    private static PreparedSource PrepareSource(string source)
    {
        var masked = source.ToCharArray();
        var stringPositions = new bool[source.Length];
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '"')
            {
                var end = SkipStringLiteral(source, i);
                for (var j = i; j < end; j++)
                {
                    stringPositions[j] = true;
                }

                i = end;
            }
            else if (c == '/' && Peek(source, i + 1) == '/')
            {
                var end = source.IndexOf('\n', i);
                end = end < 0 ? source.Length : end;
                Mask(masked, i, end);
                i = end;
            }
            else if (c == '/' && Peek(source, i + 1) == '*')
            {
                var end = FindBlockCommentEnd(source, i);
                Mask(masked, i, end);
                i = end;
            }
            else if (c == '`')
            {
                var end = FindRawBlockEnd(source, i);
                Mask(masked, i, end);
                i = end;
            }
            else
            {
                i++;
            }
        }

        return new PreparedSource(new string(masked), stringPositions);
    }

    private static int SkipStringLiteral(string source, int start)
    {
        var i = start + 1;
        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\\' && i + 1 < source.Length)
            {
                i += 2;
            }
            else if (c == '"')
            {
                return i + 1;
            }
            else if (c is '\r' or '\n')
            {
                return i;
            }
            else
            {
                i++;
            }
        }

        return i;
    }

    private static int FindBlockCommentEnd(string source, int start)
    {
        var i = start + 2;
        var depth = 1;
        while (i < source.Length && depth > 0)
        {
            if (source[i] == '/' && Peek(source, i + 1) == '*')
            {
                depth++;
                i += 2;
            }
            else if (source[i] == '*' && Peek(source, i + 1) == '/')
            {
                depth--;
                i += 2;
            }
            else
            {
                i++;
            }
        }

        return i;
    }

    private static int FindRawBlockEnd(string source, int start)
    {
        var fence = 0;
        while (start + fence < source.Length && source[start + fence] == '`')
        {
            fence++;
        }

        var delimiter = new string('`', fence);
        var end = source.IndexOf(delimiter, start + fence, StringComparison.Ordinal);
        return end < 0 ? source.Length : end + fence;
    }

    private static char Peek(string source, int index) => index < source.Length ? source[index] : '\0';

    private static void Mask(char[] chars, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (chars[i] is not '\r' and not '\n')
            {
                chars[i] = ' ';
            }
        }
    }

    /// <summary>
    /// The first <c>#import</c>/<c>#include</c> whose path cannot be resolved
    /// statically, or <c>null</c> when every one of them is a plain literal.
    /// </summary>
    private static string? FirstUnresolvableImport(PreparedSource source)
    {
        var dynamic = FirstCodeMatch(DynamicImport, source);
        if (dynamic is not null)
        {
            return dynamic.Value.Trim();
        }

        var concatenated = FirstCodeMatch(ConcatenatedImport, source);
        return concatenated?.Value.Trim();
    }

    private static Match? FirstCodeMatch(Regex regex, PreparedSource source)
    {
        foreach (Match match in regex.Matches(source.Text))
        {
            if (!source.StringPositions[match.Index])
            {
                return match;
            }
        }

        return null;
    }

    private static IEnumerable<string> CollectReferences(PreparedSource source)
    {
        foreach (var regex in new[] { LiteralImport, LiteralReader })
        {
            foreach (Match match in regex.Matches(source.Text))
            {
                if (!source.StringPositions[match.Index])
                {
                    yield return match.Groups[1].Value;
                }
            }
        }
    }

    private sealed class PreparedSource(string text, bool[] stringPositions)
    {
        public string Text { get; } = text;

        public bool[] StringPositions { get; } = stringPositions;
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
        var rootRelativeReference = reference.TrimStart('/');
        if (reference.StartsWith("//", StringComparison.Ordinal) || IsDriveQualified(rootRelativeReference))
        {
            return null;
        }

        var combined = reference.StartsWith("/", StringComparison.Ordinal)
            ? rootRelativeReference
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

    private static bool IsDriveQualified(string path)
        => path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';
}

/// <summary>Outcome of a <see cref="TemplateScanner.Scan"/>.</summary>
internal sealed class TemplateScanResult(IReadOnlyList<string> files, string? fullFolderReason)
{
    /// <summary>Root-relative, '/'-separated paths to bundle, sorted ordinally.</summary>
    public IReadOnlyList<string> Files { get; } = files;

    /// <summary>Non-null when the scanner widened the bundle to the whole root, and why.</summary>
    public string? FullFolderReason { get; } = fullFolderReason;
}
