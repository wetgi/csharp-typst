using System.Collections.Concurrent;
using System.IO.Compression;
using TypstRender.Contracts;

namespace TypstRender.Client;

/// <summary>
/// Caches the zipped template bytes per (root, entry, mode). Templates change
/// rarely while data changes per request, so steady-state renders reuse the zip
/// and only append <c>data.json</c>.
/// </summary>
/// <remarks>
/// Validity is checked against a stamp over each bundled file's size and
/// last-write time, plus a listing of the directories that ship wholesale (the
/// entry's own subtree and <c>fonts/</c>) so a newly added file is noticed too.
/// Deliberately <em>not</em> a walk of the entire template root: with twenty
/// templates under one root that meant stat-ing every file of every other
/// template on every render — milliseconds of blocking I/O locally, and far
/// worse on a bind mount or a network share. The stamp is folded into a hash
/// rather than kept as text, so an entry costs a few bytes instead of a string
/// proportional to the root.
/// </remarks>
internal sealed class TemplateBundleCache
{
    // Bounded so a caller that names render-time assets uniquely (a per-request
    // chart file, say) cannot grow this dictionary for the life of the process.
    private const int MaxEntries = 64;

    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the zipped template bundle (without any data or extra files —
    /// those are appended per request). Do not mutate. The declared
    /// <paramref name="extraPaths"/> participate in the key because they change
    /// what the scanner tolerates.
    /// </summary>
    public byte[] GetTemplateZip(
        string root, string entry, BundleMode mode, IReadOnlyCollection<string> extraPaths)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var key = root + "\n" + entry + "\n" + mode + "\n" +
            string.Join("\n", extraPaths.OrderBy(p => p, StringComparer.Ordinal));

        if (_entries.TryGetValue(key, out var hit)
            && hit.Stamp == ComputeStamp(root, entry, hit.Files, hit.WholeRoot))
        {
            return hit.ZipBytes;
        }

        var scan = TemplateScanner.Scan(root, entry, mode, extraPaths);
        var wholeRoot = mode == BundleMode.Full || scan.FullFolderReason is not null;
        var zip = ZipFiles(root, scan.Files);

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
        {
            // A crude bound rather than an LRU: the working set is a handful of
            // templates, so the only realistic way to reach the cap is a key
            // that will never be reused anyway.
            _entries.Clear();
        }

        _entries[key] = new CacheEntry(
            ComputeStamp(root, entry, scan.Files, wholeRoot), zip, scan.Files, wholeRoot);
        return zip;
    }

    private static byte[] ZipFiles(string root, IReadOnlyList<string> relativePaths)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var rel in relativePaths)
            {
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                var entry = archive.CreateEntry(rel, CompressionLevel.Optimal);
                using var target = entry.Open();
                using var source = File.OpenRead(full);
                source.CopyTo(target);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Folds the metadata that can change what this bundle contains into a
    /// single hash: every bundled file, plus a listing of the directories that
    /// ship wholesale (so an added or removed file is caught, not just an edit).
    /// </summary>
    private static long ComputeStamp(
        string root, string entry, IReadOnlyList<string> files, bool wholeRoot)
    {
        var hash = new HashAccumulator();

        foreach (var rel in files)
        {
            hash.Add(rel);
            var info = new FileInfo(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            hash.Add(info.Exists ? info.Length : -1L);
            hash.Add(info.Exists ? info.LastWriteTimeUtc.Ticks : 0L);
        }

        // Names only: a file's own size/time is already folded in above, and an
        // added or deleted file changes this listing.
        foreach (var path in ListWholesaleFiles(root, entry, wholeRoot))
        {
            hash.Add(path);
        }

        return hash.Value;
    }

    /// <summary>
    /// The files under the directories that are bundled in full regardless of
    /// what the entry imports, and so must be re-listed to notice additions.
    /// </summary>
    private static IEnumerable<string> ListWholesaleFiles(string root, string entry, bool wholeRoot)
    {
        if (wholeRoot)
        {
            return SafeEnumerate(root);
        }

        var directories = new List<string>(2);

        var separator = entry.LastIndexOf('/');
        var entryDir = separator < 0
            ? root
            : Path.Combine(root, entry.Substring(0, separator).Replace('/', Path.DirectorySeparatorChar));
        directories.Add(entryDir);

        var fontsDir = Path.Combine(root, RenderProtocol.FontsDirectory);
        if (Directory.Exists(fontsDir))
        {
            directories.Add(fontsDir);
        }

        return directories.SelectMany(SafeEnumerate);
    }

    private static IEnumerable<string> SafeEnumerate(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(
                p => p, StringComparer.Ordinal);
        }
        catch (DirectoryNotFoundException)
        {
            // The template was removed since the last render: no stamp match, so
            // the next scan reports the problem properly.
            return [];
        }
    }

    /// <summary>
    /// Order-sensitive FNV-1a fold. Enumeration order is made deterministic by
    /// the callers (scan results and directory listings are both sorted), so the
    /// same tree always yields the same value.
    /// </summary>
    private sealed class HashAccumulator
    {
        private ulong _value = 14695981039346656037UL;

        public long Value => unchecked((long)_value);

        public void Add(string text)
        {
            foreach (var c in text)
            {
                Add((byte)(c & 0xFF));
                Add((byte)(c >> 8));
            }

            Add((byte)0);
        }

        public void Add(long number)
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                Add((byte)(number >> shift));
            }
        }

        private void Add(byte b) => _value = unchecked((_value ^ b) * 1099511628211UL);
    }

    private sealed class CacheEntry
    {
        public CacheEntry(long stamp, byte[] zipBytes, IReadOnlyList<string> files, bool wholeRoot)
        {
            Stamp = stamp;
            ZipBytes = zipBytes;
            Files = files;
            WholeRoot = wholeRoot;
        }

        public long Stamp { get; }

        public byte[] ZipBytes { get; }

        /// <summary>The bundled paths, re-stat'ed to revalidate the entry.</summary>
        public IReadOnlyList<string> Files { get; }

        /// <summary>True when the bundle is the whole root, so the whole root must be re-listed.</summary>
        public bool WholeRoot { get; }
    }
}
