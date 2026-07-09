using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TypstRender.Client;

/// <summary>DI helpers for registering <see cref="ITypstRenderClient"/>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITypstRenderClient"/> backed by a typed <see cref="HttpClient"/>
    /// via <c>IHttpClientFactory</c>.
    /// </summary>
    public static IServiceCollection AddTypstRenderClient(
        this IServiceCollection services,
        Action<TypstRenderClientOptions> configure)
    {
        services.Configure(configure);

        services.AddHttpClient<ITypstRenderClient, TypstRenderClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<TypstRenderClientOptions>>().Value;

            if (options.BaseAddress is not null)
            {
                http.BaseAddress = options.BaseAddress;
            }

            http.Timeout = options.Timeout;
        });

        return services;
    }
}
