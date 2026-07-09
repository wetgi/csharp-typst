using TypstRender.Contracts;

namespace TypstRender.Service.Render;

public static class RenderEndpoint
{
    public static void MapRenderEndpoints(this WebApplication app)
    {
        app.MapGet(RenderProtocol.HealthPath, () => Results.Text("ok"))
            .WithName("Health");

        app.MapPost(RenderProtocol.RenderPath, RenderHandler)
            .WithName("Render");
    }

    private static async Task<IResult> RenderHandler(
        HttpRequest request,
        RenderService renderer,
        CancellationToken ct)
    {
        var entry = request.Query[RenderProtocol.EntryQueryParam].ToString();
        if (string.IsNullOrWhiteSpace(entry))
        {
            entry = RenderProtocol.DefaultEntry;
        }

        var inputs = request.Query[RenderProtocol.InputQueryParam]
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)
            .ToArray();

        // The body is streamed straight through: RenderService buffers it only
        // after it has acquired a concurrency slot, so requests waiting on the
        // gate do not each hold a full copy of the upload in memory.
        var outcome = await renderer.RenderAsync(request.Body, entry, inputs, ct);

        return outcome.Pdf is not null
            ? Results.File(outcome.Pdf, contentType: "application/pdf")
            : Results.Text(outcome.Error ?? string.Empty, "text/plain", statusCode: outcome.StatusCode);
    }
}
