using System.Text;
using System.Text.Json.Nodes;
using TypstRender.Client;

namespace TypstRender.Sample;

public sealed class TemplateRenderService(
    ITypstRenderClient client,
    TemplateRenderOptions options,
    BarChartSvgBuilder barChartSvgBuilder)
{
    private readonly ITypstRenderClient _client = client;
    private readonly TemplateRenderOptions _options = options;
    private readonly BarChartSvgBuilder _barChartSvgBuilder = barChartSvgBuilder;

    public IReadOnlyList<string> ListTemplates() => _client.GetTemplates();

    public async Task<TemplateRenderResult> RenderAsync(string templateName, CancellationToken cancellationToken)
    {
        var data = await EnsureTemplateExistsAndLoadDataAsync(templateName, cancellationToken);

        var request = new TypstRenderRequest
        {
            Entry = $"{templateName}/{TemplateRenderOptions.EntryFile}",
            Data = data,
            BundleMode = BundleMode.Auto,
        };

        if (data?["chart"] is JsonArray series)
        {
            request.ExtraFiles[$"{templateName}/generated/chart.svg"] =
                Encoding.UTF8.GetBytes(_barChartSvgBuilder.Build(series));
        }

        var pdf = await _client.RenderAsync(request, cancellationToken);
        return new TemplateRenderResult(templateName, pdf);
    }

    /// <summary>
    /// Previews the bundle the client would upload for <paramref name="templateName"/>
    /// without rendering. Declares the same render-time extra files
    /// (<c>generated/chart.svg</c> for data-driven charts) the real render injects,
    /// so the manifest matches what would actually be sent.
    /// </summary>
    public async Task<TemplateManifest> GetBundleManifestAsync(string templateName, CancellationToken cancellationToken)
    {
        var data = await EnsureTemplateExistsAndLoadDataAsync(templateName, cancellationToken);

        var extraFiles = data?["chart"] is JsonArray
            ? new[] { $"{templateName}/generated/chart.svg" }
            : null;

        return _client.GetBundleManifest($"{templateName}/{TemplateRenderOptions.EntryFile}", extraFiles: extraFiles);
    }

    private async Task<JsonNode?> EnsureTemplateExistsAndLoadDataAsync(string templateName, CancellationToken cancellationToken)
    {
        var availableTemplates = _client.GetTemplates();
        if (!availableTemplates.Contains(templateName, StringComparer.Ordinal))
        {
            throw new TemplateNotFoundException(templateName, availableTemplates);
        }

        var dataPath = Path.Combine(_options.TemplateRoot, templateName, TemplateRenderOptions.DataFile);
        return File.Exists(dataPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(dataPath, cancellationToken))
            : null;
    }
}
