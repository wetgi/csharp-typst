using TypstRender.Service.Render;

var builder = WebApplication.CreateBuilder(args);

var renderSection = builder.Configuration.GetSection(RenderOptions.SectionName);
builder.Services.Configure<RenderOptions>(renderSection);

var renderOptions = renderSection.Get<RenderOptions>() ?? new RenderOptions();

// Reject oversized uploads before they are buffered.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = renderOptions.MaxUploadBytes);

// Graceful shutdown: on SIGTERM the host stops accepting new requests and
// drains in-flight renders for up to this window before cancelling them.
builder.Services.Configure<HostOptions>(o =>
    o.ShutdownTimeout = TimeSpan.FromSeconds(renderOptions.ShutdownTimeoutSeconds));

builder.Services.AddSingleton<TypstRunner>();
builder.Services.AddSingleton<RenderService>();

var app = builder.Build();

app.MapRenderEndpoints();

app.Run();

// Exposed so the integration test project can host the service in-memory.
public partial class Program;
