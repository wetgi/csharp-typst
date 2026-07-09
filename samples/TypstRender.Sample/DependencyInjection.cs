using TypstRender.Client;

namespace TypstRender.Sample;

public static class DependencyInjection
{
    private const string DefaultServiceUrl = "http://localhost:8080";
    private const string ServiceUrlEnvironmentVariable = "TYPST_SERVICE_URL";

    public static IServiceCollection AddTypstSampleRendering(this IServiceCollection services)
    {
        var serviceUrl = Environment.GetEnvironmentVariable(ServiceUrlEnvironmentVariable) ?? DefaultServiceUrl;
        var templatesRoot = Path.Combine(AppContext.BaseDirectory, TemplateRenderOptions.TemplatesDirectoryName);

        services.AddSingleton(new TemplateRenderOptions(templatesRoot));
        services.AddSingleton<BarChartSvgBuilder>();
        services.AddScoped<TemplateRenderService>();

        services.AddTypstRenderClient(o =>
        {
            o.BaseAddress = new Uri(serviceUrl);
            o.TemplateRoot = templatesRoot;
        });

        return services;
    }
}
