using System.IO.Compression;

namespace TypstRender.Service.Render;

/// <summary>
/// Unpacks the uploaded zip into a destination directory that serves as the
/// Typst <c>--root</c>.
///
/// The upload is untrusted, so every entry is measured before it reaches the
/// disk: it must stay inside the destination (zip-slip), the bundle must stay
/// under a file count, and the unpacked total must stay within a byte budget.
/// That last one matters because the request-size limit caps only the
/// <em>compressed</em> body — a well-formed 20 MB zip of compressible data
/// unpacks to tens of gigabytes.
/// </summary>
public static class BundleExtractor
{
    private const int CopyBufferSize = 64 * 1024;

    /// <summary>Budget a single untrusted bundle is allowed to consume.</summary>
    /// <param name="MaxEntries">Maximum number of files in the bundle.</param>
    /// <param name="MaxExtractedBytes">Maximum total size of the unpacked files.</param>
    public sealed record Limits(int MaxEntries, long MaxExtractedBytes);

    /// <summary>
    /// Unpacks <paramref name="zip"/> into <paramref name="destinationDir"/>.
    /// Returns a message describing why the bundle was rejected, or <c>null</c>
    /// on success. Cancellation propagates; every other fault in a bundle we
    /// were handed is reported as a rejection rather than thrown, because a
    /// malformed archive is the caller's mistake, not a server error.
    /// </summary>
    public static async Task<string?> ExtractAsync(
        Stream zip, string destinationDir, Limits limits, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            return await ExtractEntriesAsync(archive, destinationDir, limits, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsBundleFault(ex))
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static async Task<string?> ExtractEntriesAsync(
        ZipArchive archive, string destinationDir, Limits limits, CancellationToken cancellationToken)
    {
        var destFull = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;
        var buffer = new byte[CopyBufferSize];
        var files = 0;
        long extracted = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(entry.Name))
            {
                continue; // directory entry
            }

            if (++files > limits.MaxEntries)
            {
                return $"bundle contains more than {limits.MaxEntries} files";
            }

            var target = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!target.StartsWith(destFull, StringComparison.Ordinal))
            {
                return $"entry '{entry.FullName}' escapes the bundle root";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Copied through a running total rather than ExtractToFile:
            // entry.Length is declared by the uploader, so only bytes actually
            // written can be trusted to enforce the budget.
            using var source = entry.Open();
            using var destination = new FileStream(
                target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);

            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                extracted += read;
                if (extracted > limits.MaxExtractedBytes)
                {
                    return $"bundle unpacks to more than {limits.MaxExtractedBytes} bytes";
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>
    /// Faults that mean "this archive is not something we can unpack": an
    /// unsupported compression method or a CRC mismatch
    /// (<see cref="InvalidDataException"/>), a name the filesystem rejects or a
    /// file/directory clash between two entries (<see cref="IOException"/>,
    /// <see cref="ArgumentException"/>), or a full disk.
    /// </summary>
    private static bool IsBundleFault(Exception ex) => ex
        is InvalidDataException
        or IOException
        or UnauthorizedAccessException
        or ArgumentException
        or NotSupportedException;
}
