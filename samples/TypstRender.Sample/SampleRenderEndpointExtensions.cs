using TypstRender.Client;

namespace TypstRender.Sample;

public static class SampleRenderEndpointExtensions
{
    public static IEndpointRouteBuilder MapSampleRenderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/templates", ListTemplates)
            .WithName("ListTemplates")
            .Produces<IReadOnlyList<string>>(StatusCodes.Status200OK);

        endpoints.MapGet("/render/{templateName}", RenderTemplateAsync)
            .WithName("RenderTemplate")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .Produces(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        endpoints.MapGet("/manifest/{templateName}", GetBundleManifestAsync)
            .WithName("GetBundleManifest")
            .Produces<TemplateManifest>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return endpoints;
    }

    private static IResult ListTemplates(TemplateRenderService renderer) =>
        Results.Ok(renderer.ListTemplates());

    private static async Task<IResult> GetBundleManifestAsync(
        string templateName,
        TemplateRenderService renderer,
        CancellationToken cancellationToken)
    {
        // Nested template names arrive percent-encoded (%2F); decode as in RenderTemplateAsync.
        templateName = Uri.UnescapeDataString(templateName);

        try
        {
            var manifest = await renderer.GetBundleManifestAsync(templateName, cancellationToken);
            return Results.Ok(new
            {
                templateName,
                manifest.Files,
                manifest.IsFullFolder,
                manifest.FullFolderReason
            });
        }
        catch (TemplateNotFoundException ex)
        {
            return Results.NotFound(new
            {
                error = ex.Message,
                availableTemplates = ex.AvailableTemplates
            });
        }
    }

    private static async Task<IResult> RenderTemplateAsync(
        string templateName,
        TemplateRenderService renderer,
        CancellationToken cancellationToken)
    {
        // Nested template names contain '/', which callers send percent-encoded
        // as %2F. ASP.NET does not decode %2F within a route segment, so decode it
        // here before resolving the template (e.g. "docs%2Fintro" -> "docs/intro").
        templateName = Uri.UnescapeDataString(templateName);

        try
        {
            var result = await renderer.RenderAsync(templateName, cancellationToken);
            return Results.File(result.Pdf, "application/pdf", $"{result.TemplateName}.pdf");
        }
        catch (TemplateNotFoundException ex)
        {
            return Results.NotFound(new
            {
                error = ex.Message,
                availableTemplates = ex.AvailableTemplates
            });
        }
        catch (TypstRenderException ex)
        {
            return Results.Problem(
                title: ex.Message,
                detail: string.IsNullOrWhiteSpace(ex.Detail) ? ex.Message : ex.Detail,
                statusCode: ex.StatusCode);
        }
    }
}
