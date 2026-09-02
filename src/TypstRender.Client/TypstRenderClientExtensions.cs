namespace TypstRender.Client;

/// <summary>
/// Convenience renders built on <see cref="ITypstRenderClient"/>. Both stream
/// the response instead of buffering the whole PDF, and both own the HTTP
/// response's lifetime — so neither can leak a pooled connection the way a
/// forgotten <c>RenderToStreamAsync</c> disposal does.
/// </summary>
public static class TypstRenderClientExtensions
{
    /// <summary>
    /// Renders <paramref name="entry"/> and copies the PDF into
    /// <paramref name="destination"/> — an HTTP response body, a blob upload, a
    /// file. The usual choice when the document is not needed as a
    /// <c>byte[]</c>.
    /// </summary>
    public static async Task RenderToAsync(
        this ITypstRenderClient client,
        string entry,
        Stream destination,
        object? data = null,
        CancellationToken cancellationToken = default)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        using var pdf = await client.RenderToStreamAsync(entry, data, cancellationToken).ConfigureAwait(false);
        await pdf.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders <paramref name="request"/> and copies the PDF into
    /// <paramref name="destination"/>.
    /// </summary>
    public static async Task RenderToAsync(
        this ITypstRenderClient client,
        TypstRenderRequest request,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (client is null)
        {
            throw new ArgumentNullException(nameof(client));
        }

        if (destination is null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        using var pdf = await client.RenderToStreamAsync(request, cancellationToken).ConfigureAwait(false);
        await pdf.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders <paramref name="entry"/> straight to <paramref name="path"/>,
    /// overwriting an existing file. Creates the containing directory when it is
    /// missing.
    /// </summary>
    public static async Task RenderToFileAsync(
        this ITypstRenderClient client,
        string entry,
        string path,
        object? data = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A destination file path is required.", nameof(path));
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory!);
        }

        using var file = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await client.RenderToAsync(entry, file, data, cancellationToken).ConfigureAwait(false);
    }
}
