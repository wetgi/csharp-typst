using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace TypstRender.Client;

/// <summary>
/// Caches the zipped template bytes per (root, entry, mode). Templates change
/// rarely while data changes per request, so steady-state renders reuse the zip
/// and only append <c>data.json</c>. Validity is checked against a cheap stamp
/// over every file's path, size and last-write time under the root — any change
/// triggers a rescan and rebuild.
/// </summary>
internal sealed class TemplateBundleCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _entries =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the zipped template bundle (without any data or extra files —
    /// those are appended per request). Do not mutate. The declared
    /// <paramref name="extraPaths"/> participate in the key because they change
    /// what the scanner tolerates.
    /// </summary>
    public byte[] GetTemplateZip(string root, string entry, BundleMode mode, IReadOnlyCollection<string> extraPaths)
    {
        root = Path.GetFullPath(root);
        var key = root + "\n" + entry + "\n" + mode + "\n" +
            string.Join("\n", extraPaths.OrderBy(p => p, StringComparer.Ordinal));
        var stamp = ComputeStamp(root);

        if (_entries.TryGetValue(key, out var hit) && string.Equals(hit.Stamp, stamp, StringComparison.Ordinal))
        {
            return hit.ZipBytes;
        }

        var scan = TemplateScanner.Scan(root, entry, mode, extraPaths);
        var zip = ZipFiles(root, scan.Files);
        _entries[key] = new CacheEntry(stamp, zip);
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

    private static string ComputeStamp(string root)
    {
        var lines = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(path);
            lines.Add(path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
        }

        lines.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private sealed class CacheEntry
    {
        public CacheEntry(string stamp, byte[] zipBytes)
        {
            Stamp = stamp;
            ZipBytes = zipBytes;
        }

        public string Stamp { get; }

        public byte[] ZipBytes { get; }
    }
}
