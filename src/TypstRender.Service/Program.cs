using TypstRender.Service.Render;

var builder = WebApplication.CreateBuilder(args);

var renderSection = builder.Configuration.GetSection(RenderOptions.SectionName);

// Validated at startup: a nonsensical limit fails the host with a message
// naming the key, instead of turning every render into a puzzling 500 or 504.
builder.Services.AddOptions<RenderOptions>()
    .Bind(renderSection)
    .ValidateDataAnnotations()
    .Validate(
        o => o.ShutdownTimeoutSeconds >= o.TimeoutSeconds,
        $"{RenderOptions.SectionName}:ShutdownTimeoutSeconds must be at least "
            + $"{RenderOptions.SectionName}:TimeoutSeconds, or a render started just before shutdown "
            + "is always aborted.")
    .Validate(
        o => o.MaxExtractedBytes >= o.MaxUploadBytes,
        $"{RenderOptions.SectionName}:MaxExtractedBytes must be at least "
            + $"{RenderOptions.SectionName}:MaxUploadBytes.")
    .ValidateOnStart();

// Read eagerly as well: the Kestrel limit and the shutdown window are needed to
// build the host, before startup validation gets a chance to run.
var renderOptions = renderSection.Get<RenderOptions>() ?? new RenderOptions();

if (renderOptions.MaxUploadBytes <= 0)
{
    throw new InvalidOperationException(
        $"{RenderOptions.SectionName}:MaxUploadBytes must be greater than zero "
            + $"(got {renderOptions.MaxUploadBytes}).");
}

// Reject oversized uploads before they are buffered.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = renderOptions.MaxUploadBytes);

// Graceful shutdown: on SIGTERM the host stops accepting new requests and
// drains in-flight renders for up to this window before cancelling them.
builder.Services.Configure<HostOptions>(o =>
    o.ShutdownTimeout = TimeSpan.FromSeconds(renderOptions.ShutdownTimeoutSeconds));

builder.Services.AddSingleton<TypstRunner>();
builder.Services.AddSingleton<RenderService>();
builder.Services.AddHostedService<TempWorkspaceCleaner>();

var app = builder.Build();

app.MapRenderEndpoints();

app.Run();

// Exposed so the integration test project can host the service in-memory.
public partial class Program;
