namespace TypstRender.Client;

/// <summary>Thrown when the rendering service returns a non-success response.</summary>
public sealed class TypstRenderException : Exception
{
    /// <summary>HTTP status code returned by the service.</summary>
    public int StatusCode { get; }

    /// <summary>
    /// Response body. For a failed compilation (HTTP 422) this is the typst
    /// stderr; for other errors it is a short diagnostic message.
    /// </summary>
    public string? Detail { get; }

    /// <summary>Creates the exception from a failed service response.</summary>
    public TypstRenderException(int statusCode, string message, string? detail)
        : base(message)
    {
        StatusCode = statusCode;
        Detail = detail;
    }
}
