using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TypstRender.Client;

// Declared in the DI namespace, as every Microsoft.Extensions.* package does, so
// `builder.Services.AddTypstRenderClient(...)` shows up without an extra using.
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI helpers for registering <see cref="ITypstRenderClient"/>.</summary>
public static class TypstRenderClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ITypstRenderClient"/> backed by a typed
    /// <see cref="System.Net.Http.HttpClient"/> via <c>IHttpClientFactory</c>.
    /// Options are validated the first time <see cref="ITypstRenderClient"/> is
    /// resolved, so a missing or malformed service address fails with a message
    /// naming the setting rather than as an opaque <c>HttpClient</c> error on the
    /// first render. (Validation is not wired to <c>ValidateOnStart</c>, which
    /// would make this package depend on the whole hosting stack; an app that
    /// wants startup validation can add it on its own options builder.)
    /// </summary>
    /// <returns>
    /// The <see cref="IHttpClientBuilder"/> for the underlying typed client, so
    /// callers can attach a resilience handler or a <c>DelegatingHandler</c> —
    /// worth doing, since the service answers <c>503</c> at capacity and
    /// <c>504</c> on timeout by design.
    /// </returns>
    public static IHttpClientBuilder AddTypstRenderClient(
        this IServiceCollection services,
        Action<TypstRenderClientOptions> configure)
    {
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        return AddCore(services, builder => builder.Configure(configure));
    }

    /// <summary>
    /// Registers <see cref="ITypstRenderClient"/> with options bound from
    /// configuration — the shape an IIS-hosted or containerized app wants, where
    /// the service address and template root are per-environment settings:
    /// <c>services.AddTypstRenderClient(builder.Configuration.GetSection("TypstRender"))</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Section carrying <c>BaseAddress</c>, <c>TemplateRoot</c>, ….</param>
    /// <param name="configure">Optional code-side overrides applied after binding.</param>
    public static IHttpClientBuilder AddTypstRenderClient(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TypstRenderClientOptions>? configure = null)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        return AddCore(services, builder =>
        {
            builder.Bind(configuration);
            if (configure is not null)
            {
                builder.Configure(configure);
            }
        });
    }

    private static IHttpClientBuilder AddCore(
        IServiceCollection services, Action<OptionsBuilder<TypstRenderClientOptions>> configureOptions)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        var optionsBuilder = services.AddOptions<TypstRenderClientOptions>();
        configureOptions(optionsBuilder);

        optionsBuilder
            .Validate(
                o => o.BaseAddress is not null,
                $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.BaseAddress)} is required, "
                    + "e.g. new Uri(\"http://localhost:8080\").")
            .Validate(
                o => o.BaseAddress is null || o.BaseAddress.IsAbsoluteUri,
                $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.BaseAddress)} "
                    + "must be an absolute URI including the scheme, e.g. http://localhost:8080.")
            .Validate(
                o => o.BaseAddress is null
                    || !o.BaseAddress.IsAbsoluteUri
                    || o.BaseAddress.Scheme == "http"
                    || o.BaseAddress.Scheme == "https",
                $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.BaseAddress)} "
                    + "must use http or https.")
            .Validate(
                o => o.BaseAddress is null
                    || !o.BaseAddress.IsAbsoluteUri
                    || (string.IsNullOrEmpty(o.BaseAddress.Query) && string.IsNullOrEmpty(o.BaseAddress.Fragment)),
                $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.BaseAddress)} "
                    + "must not carry a query string or fragment.")
            .Validate(
                o => o.Timeout > TimeSpan.Zero || o.Timeout == Timeout.InfiniteTimeSpan,
                $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.Timeout)} "
                    + "must be positive (or Timeout.InfiniteTimeSpan).")
            .Validate(
                o => o.TemplateRoot is null || Directory.Exists(o.TemplateRoot),
                $"{nameof(TypstRenderClientOptions)}.{nameof(TypstRenderClientOptions.TemplateRoot)} "
                    + "does not exist. A relative path resolves against the process working directory, "
                    + "which differs under IIS and in a container — consider "
                    + "Path.Combine(AppContext.BaseDirectory, \"templates\").");

        return services.AddHttpClient<ITypstRenderClient, TypstRenderClient>((sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<TypstRenderClientOptions>>().Value;

            http.Timeout = options.Timeout;
        });
    }
}
