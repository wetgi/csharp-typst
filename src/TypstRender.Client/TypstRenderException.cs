using System.Net;

namespace TypstRender.Client;

/// <summary>
/// Thrown when a render does not produce a document: the service returned a
/// non-success response, could not be reached, or did not answer in time.
/// </summary>
public sealed class TypstRenderException : Exception
{
    /// <summary>
    /// HTTP status code returned by the service, or <c>0</c> when no response
    /// arrived at all (a transport failure or a client-side timeout).
    /// </summary>
    public int StatusCode { get; }

    /// <summary>
    /// Response body. For a failed compilation (HTTP 422) this is the typst
    /// stderr; for other errors it is a short diagnostic message. It is also
    /// appended to <see cref="Exception.Message"/>, so a plain
    /// <c>logger.LogError(ex, ...)</c> shows the compiler output without the
    /// caller having to know this property exists.
    /// </summary>
    public string? Detail { get; }

    /// <summary>The entry that was being rendered, when known.</summary>
    public string? Entry { get; }

    /// <summary>The URI the render was posted to, when known.</summary>
    public Uri? RequestUri { get; }

    /// <summary>True for HTTP 422 — the template itself did not compile.</summary>
    public bool IsCompileError => StatusCode == 422;

    /// <summary>
    /// True when no response was received: the service was unreachable, or the
    /// client's own timeout elapsed first.
    /// </summary>
    public bool IsTransportFailure => StatusCode == 0;

    /// <summary><see cref="StatusCode"/> as an <see cref="HttpStatusCode"/>.</summary>
    public HttpStatusCode HttpStatus => (HttpStatusCode)StatusCode;

    /// <summary>Creates the exception from a failed render.</summary>
    public TypstRenderException(
        int statusCode,
        string message,
        string? detail,
        string? entry = null,
        Uri? requestUri = null,
        Exception? innerException = null)
        : base(Compose(message, detail), innerException)
    {
        StatusCode = statusCode;
        Detail = detail;
        Entry = entry;
        RequestUri = requestUri;
    }

    // A pathological stderr should not become the payload of the exception.
    private const int MaxDetailInMessage = 4000;

    private static string Compose(string message, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return message;
        }

        var body = detail!.Length <= MaxDetailInMessage
            ? detail
            : detail.Substring(0, MaxDetailInMessage) + "… (truncated; see Detail)";

        return message + Environment.NewLine + body;
    }
}
