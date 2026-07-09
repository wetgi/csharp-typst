using System.IO.Compression;

namespace TypstRender.Service.Render;

/// <summary>
/// Unpacks the uploaded zip into a destination directory that serves as the
/// Typst <c>--root</c>, rejecting any entry that would escape it (zip-slip).
/// </summary>
public static class BundleExtractor
{
    /// <summary>Returns an error message, or <c>null</c> on success.</summary>
    public static string? Extract(Stream zip, string destinationDir)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zip, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            return $"not a valid zip archive: {ex.Message}";
        }

        using (archive)
        {
            var destFull = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue; // directory entry
                }

                var target = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
                if (!target.StartsWith(destFull, StringComparison.Ordinal))
                {
                    return $"entry '{entry.FullName}' escapes the bundle root";
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        return null;
    }
}
