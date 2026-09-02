using TypstRender.Contracts;

namespace TypstRender.Service.Render;

public static class RenderEndpoint
{
    public static void MapRenderEndpoints(this WebApplication app)
    {
        // Liveness: is the process up? Deliberately dependency-free, so a
        // restart decision never hinges on a subprocess spawn.
        app.MapGet(RenderProtocol.HealthPath, () => Results.Text("ok"))
            .WithName("Health");

        // Readiness: can this instance actually render? An image whose typst
        // install is missing, truncated or built for another architecture used
        // to report itself healthy while failing every render.
        app.MapGet(RenderProtocol.ReadyPath, ReadyHandler)
            .WithName("Ready");

        app.MapPost(RenderProtocol.RenderPath, RenderHandler)
            .WithName("Render");
    }

    private static async Task<IResult> ReadyHandler(TypstRunner typst, CancellationToken cancellationToken)
    {
        var version = await typst.TryGetVersionAsync(cancellationToken);

        return version is null
            ? Results.Text(
                "not ready: the typst binary could not be run",
                "text/plain",
                statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Text($"ok\n{version}", "text/plain");
    }

    private static async Task<IResult> RenderHandler(
        HttpRequest request,
        RenderService renderer,
        CancellationToken cancellationToken)
    {
        var entryValues = request.Query[RenderProtocol.EntryQueryParam];
        if (entryValues.Count > 1)
        {
            // StringValues would otherwise join them with a comma and produce a
            // baffling "entry 'a.typ,b.typ' not found in bundle".
            return BadRequest($"'{RenderProtocol.EntryQueryParam}' must be supplied at most once");
        }

        var entry = entryValues.ToString();
        if (string.IsNullOrWhiteSpace(entry))
        {
            entry = RenderProtocol.DefaultEntry;
        }

        var inputs = new List<string>();
        foreach (var value in request.Query[RenderProtocol.InputQueryParam])
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            // Rejected here rather than by typst, which answers a pair without
            // '=' with a clap usage dump and exit code 2 — surfacing a caller
            // mistake as a 422 "compile error" and leaking the CLI surface.
            if (value.IndexOf('=') <= 0)
            {
                return BadRequest(
                    $"'{RenderProtocol.InputQueryParam}' must be 'key=value'; got '{value}'");
            }

            inputs.Add(value);
        }

        // The body is streamed straight through: RenderService buffers it only
        // after it has acquired a concurrency slot, so requests waiting on the
        // gate do not each hold a full copy of the upload in memory.
        var outcome = await renderer.RenderAsync(request.Body, entry, inputs, cancellationToken);

        return outcome.Pdf is not null
            ? Results.File(outcome.Pdf, contentType: "application/pdf")
            : Results.Text(outcome.Error ?? string.Empty, "text/plain", statusCode: outcome.StatusCode);
    }

    private static IResult BadRequest(string message)
        => Results.Text(message, "text/plain", statusCode: StatusCodes.Status400BadRequest);
}
