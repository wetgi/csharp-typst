using System.IO.Compression;

namespace TypstRender.Service.Render;

/// <summary>
/// Unpacks the uploaded zip into a destination directory that serves as the
/// Typst <c>--root</c>.
///
/// The upload is untrusted, so every entry is measured before it reaches the
/// disk: it must stay inside the destination (zip-slip), target names must be
/// unambiguous, the archive must stay under an entry count, and the unpacked
/// total must stay within a byte budget.
/// That last one matters because the request-size limit caps only the
/// <em>compressed</em> body — a well-formed 20 MB zip of compressible data
/// unpacks to tens of gigabytes.
/// </summary>
public static class BundleExtractor
{
    private const int CopyBufferSize = 64 * 1024;

    private static readonly StringComparer TargetComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static readonly StringComparison TargetComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    /// <summary>Budget a single untrusted bundle is allowed to consume.</summary>
    /// <param name="MaxEntries">Maximum number of records in the zip archive.</param>
    /// <param name="MaxExtractedBytes">Maximum total size of the unpacked files.</param>
    public sealed record Limits(int MaxEntries, long MaxExtractedBytes);

    /// <summary>
    /// Unpacks <paramref name="zip"/> into <paramref name="destinationDir"/>.
    /// Returns a message describing why the bundle was rejected, or <c>null</c>
    /// on success. Cancellation and host filesystem failures propagate; faults
    /// proven to come from the archive are returned as client errors.
    /// </summary>
    public static async Task<string?> ExtractAsync(
        Stream zip, string destinationDir, Limits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zip);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(limits.MaxExtractedBytes, 1);

        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            return await ExtractEntriesAsync(archive, destinationDir, limits, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return "not a valid zip archive";
        }
        catch (NotSupportedException)
        {
            return "zip archive uses an unsupported compression method";
        }
        catch (ArgumentException)
        {
            return "zip archive contains an invalid path";
        }
    }

    private static async Task<string?> ExtractEntriesAsync(
        ZipArchive archive, string destinationDir, Limits limits, CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(destinationDir);
        var destinationPrefix = Path.EndsInDirectorySeparator(destinationRoot)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        var planError = TryBuildExtractionPlan(
            archive, destinationPrefix, limits.MaxEntries, cancellationToken, out var plan);
        if (planError is not null)
        {
            return planError;
        }

        var buffer = new byte[CopyBufferSize];
        long extracted = 0;

        foreach (var item in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);

            // Copied through a running total rather than ExtractToFile:
            // entry.Length is declared by the uploader, so only bytes actually
            // written can be trusted to enforce the budget.
            using var source = item.Entry.Open();
            using var destination = new FileStream(
                item.Target, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);

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

    private static string? TryBuildExtractionPlan(
        ZipArchive archive,
        string destinationPrefix,
        int maxEntries,
        CancellationToken cancellationToken,
        out List<PlannedEntry> plan)
    {
        plan = [];
        var explicitTargets = new HashSet<string>(TargetComparer);
        var fileTargets = new HashSet<string>(TargetComparer);
        var directoryTargets = new HashSet<string>(TargetComparer);
        var entryCount = 0;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (++entryCount > maxEntries)
            {
                return $"bundle contains more than {maxEntries} entries";
            }

            var full = Path.GetFullPath(Path.Combine(destinationPrefix, entry.FullName));
            if (!full.StartsWith(destinationPrefix, TargetComparison))
            {
                return $"entry '{entry.FullName}' escapes the bundle root";
            }

            // GetFullPath keeps the trailing separator a directory record
            // carries. Left on, 'a/' and 'a' are different strings and none of
            // the comparisons below can ever match. The zip-slip check above
            // runs on the untrimmed path so a bare './' entry still resolves
            // inside the root rather than onto it.
            var target = Path.TrimEndingDirectorySeparator(full);
            var isDirectoryRecord = string.IsNullOrEmpty(entry.Name);

            if (isDirectoryRecord && fileTargets.Contains(target))
            {
                return $"entry '{entry.FullName}' conflicts with a file";
            }

            if (!isDirectoryRecord && directoryTargets.Contains(target))
            {
                return $"entry '{entry.FullName}' conflicts with a directory";
            }

            if (!explicitTargets.Add(target))
            {
                return $"entry '{entry.FullName}' resolves to a duplicate target";
            }

            for (var parent = Path.GetDirectoryName(target);
                 parent is not null && parent.StartsWith(destinationPrefix, TargetComparison);
                 parent = Path.GetDirectoryName(parent))
            {
                if (fileTargets.Contains(parent))
                {
                    return $"entry '{entry.FullName}' has a file where a directory is required";
                }

                directoryTargets.Add(parent);
            }

            if (isDirectoryRecord)
            {
                directoryTargets.Add(target);
                continue;
            }

            fileTargets.Add(target);
            plan.Add(new PlannedEntry(entry, target));
        }

        return null;
    }

    private sealed record PlannedEntry(ZipArchiveEntry Entry, string Target);
}
