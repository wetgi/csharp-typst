using System.Text;
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
/// Scanning happens in two steps. First the source is tokenized so that
/// comments and raw blocks are blanked while string literals are preserved
/// intact: matching references against raw text made a <c>"/*"</c> or a
/// <c>"//"</c> inside a string swallow the statements that followed it, which
/// silently dropped files from the bundle. Then references are matched only in
/// code position — after <c>#</c>, a brace, a semicolon or at the start of a
/// line — so prose such as <c>Prices include "VAT"</c> is not mistaken for an
/// import.
///
/// The scanner stays deliberately fail-safe: an <c>#import</c>/<c>#include</c>
/// path it cannot resolve statically (a variable, or a string it concatenates)
/// widens the bundle to the whole root rather than risk a missing file. Asset
/// readers with non-literal arguments (the <c>json(data-path)</c> data
/// convention) are tolerated instead, because a reader cannot pull in further
/// references of its own.
///
/// Two known limits, both of which under-bundle rather than fail: only the
/// first path in a reader call is followed (so
/// <c>bibliography(("a.bib", "b.bib"))</c> misses the second), and a path
/// produced entirely by an expression is invisible. Use
/// <see cref="BundleMode.Full"/> for a template that needs either.
/// </summary>
internal static class TemplateScanner
{
    // Code position: an import/include only counts when it follows '#', a brace,
    // a semicolon, or begins a line. Typst allows a bare `import` in code mode,
    // so requiring '#' would miss those; allowing it anywhere matches prose.
    private const string CodePosition = @"(?:^|[#{};])[ \t]*";

    // Named arguments may precede the positional path, and `bibliography` takes
    // an array, so tolerate a leading '(' too.
    private const string ReaderCallOpen = @"\s*\(\s*\(?\s*(?:[\w-]+\s*:\s*[^,()""]*,\s*)*";

    // A literal that is immediately concatenated is only a prefix, not a path.
    private const string NotConcatenated = @"(?!\s*\+)";

    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline;

    // #import "..." / #include "..." (also valid without the leading # in code mode).
    private static readonly Regex LiteralImport = new(
        CodePosition + @"(?:import|include)\s+""([^""\r\n]+)""" + NotConcatenated, Options);

    // Asset readers taking a string-literal path.
    private static readonly Regex LiteralReader = new(
        @"(?<![\w.\-])(?:image|read|json|csv|yaml|toml|xml|cbor|bibliography|plugin)"
            + ReaderCallOpen + @"""([^""\r\n]+)""" + NotConcatenated,
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

        root = TrimTrailingSeparator(Path.GetFullPath(root));
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
            var code = StripCommentsAndRawBlocks(File.ReadAllText(ToFullPath(root, rel)));

            var unresolvable = FirstUnresolvableImport(code);
            if (unresolvable is not null)
            {
                // Cannot prove what the expression resolves to: ship everything.
                return new TemplateScanResult(
                    AllFiles(root), $"unresolvable import expression '{unresolvable}' in '{rel}'");
            }

            foreach (var reference in CollectReferences(code))
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

                if (Directory.Exists(ToFullPath(root, resolved)))
                {
                    // Near-certainly the literal prefix of a path assembled at
                    // runtime; a directory is never a Typst reference target.
                    continue;
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
    /// Normalizes a root-relative entry path and rejects one that tries to climb
    /// out of the root — otherwise <c>../../etc/passwd</c> would be scanned, and
    /// enumerating outside the root corrupts every relative path derived from it.
    /// </summary>
    public static string NormalizeEntry(string? entry)
    {
        var rel = entry is null ? string.Empty : NormalizeSlashes(entry).TrimStart('/');
        if (rel.Length == 0)
        {
            throw new ArgumentException("Entry must name a .typ file inside the template root.", nameof(entry));
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
    /// Blanks comments and raw blocks while leaving string literals intact, in a
    /// single left-to-right pass. Doing this with independent regexes let a
    /// <c>"/*"</c> or a <c>"//"</c> inside a string consume the code after it.
    /// </summary>
    private static string StripCommentsAndRawBlocks(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '"')
            {
                i = CopyStringLiteral(source, i, sb);
            }
            else if (c == '/' && Peek(source, i + 1) == '/')
            {
                i = SkipToEndOfLine(source, i);
            }
            else if (c == '/' && Peek(source, i + 1) == '*')
            {
                i = SkipBlockComment(source, i);
                sb.Append(' ');
            }
            else if (c == '`')
            {
                i = SkipRaw(source, i);
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Copies a <c>"..."</c> literal verbatim, honouring backslash escapes. An
    /// unterminated literal stops at the line break rather than running on into
    /// the next statement.
    /// </summary>
    private static int CopyStringLiteral(string source, int start, StringBuilder sb)
    {
        sb.Append('"');
        var i = start + 1;

        while (i < source.Length)
        {
            var c = source[i];
            if (c == '\\' && i + 1 < source.Length)
            {
                sb.Append(c).Append(source[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\r' || c == '\n')
            {
                sb.Append('"'); // close it so the reference regexes cannot span lines
                return i;
            }

            sb.Append(c);
            i++;

            if (c == '"')
            {
                return i;
            }
        }

        sb.Append('"');
        return i;
    }

    private static int SkipToEndOfLine(string source, int start)
    {
        var i = start;
        while (i < source.Length && source[i] != '\n')
        {
            i++;
        }

        return i;
    }

    /// <summary>Skips a <c>/* ... */</c> comment; Typst nests them.</summary>
    private static int SkipBlockComment(string source, int start)
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

    /// <summary>
    /// Skips a raw block — <c>`inline`</c> or a fenced <c>```…```</c>. Its
    /// content is verbatim text, so a documentation page showing
    /// <c>#include &lt;stdio.h&gt;</c> must not be read as a dependency.
    /// </summary>
    private static int SkipRaw(string source, int start)
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

    /// <summary>
    /// The first <c>#import</c>/<c>#include</c> whose path cannot be resolved
    /// statically, or <c>null</c> when every one of them is a plain literal.
    /// </summary>
    private static string? FirstUnresolvableImport(string code)
    {
        var dynamic = DynamicImport.Match(code);
        if (dynamic.Success)
        {
            return dynamic.Value.Trim();
        }

        var concatenated = ConcatenatedImport.Match(code);
        return concatenated.Success ? concatenated.Value.Trim() : null;
    }

    private static IEnumerable<string> CollectReferences(string code)
    {
        foreach (Match m in LiteralImport.Matches(code))
        {
            yield return m.Groups[1].Value;
        }

        foreach (Match m in LiteralReader.Matches(code))
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
        var prefix = root + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            // A directory symlink can lead outside the root, and a path that is
            // not under it has no meaningful root-relative form.
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            yield return NormalizeSlashes(path.Substring(prefix.Length));
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

    private static string TrimTrailingSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>Outcome of a <see cref="TemplateScanner.Scan"/>.</summary>
internal sealed class TemplateScanResult(IReadOnlyList<string> files, string? fullFolderReason)
{
    /// <summary>Root-relative, '/'-separated paths to bundle, sorted ordinally.</summary>
    public IReadOnlyList<string> Files { get; } = files;

    /// <summary>Non-null when the scanner widened the bundle to the whole root, and why.</summary>
    public string? FullFolderReason { get; } = fullFolderReason;
}
