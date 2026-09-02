# TypstRender.Client

> This package is one half of the system. The other half is the rendering service — an
> ASP.NET minimal-API container wrapping the Typst CLI — published as a ready-to-run image
> at `ghcr.io/wetgi/typst-render-service` (or build it from the repo). See the
> [full README](https://github.com/wetgi/csharp-typst) for the service, Dockerfile, and architecture.

A thin, `HttpClient`-based .NET SDK for rendering PDFs with [Typst](https://typst.app)
— **without** shipping the Typst binary in your app. Your project references this
package; the actual rendering runs in a companion **container service** that owns the
Typst engine and fonts. The client scans a template's import closure, zips only the
files that render needs, posts them, and hands you back the PDF bytes.

- **No native binary, no fonts in your app** — they live in the service image only.
- **Ships the minimum** — `BundleMode.Auto` scans `#import`/`#include`/`image(...)`/`json(...)`
  references and bundles just the reachable files; missing references fail fast, client-side.
- **DI-first** — registers as a typed `HttpClient` via `IHttpClientFactory`, and returns the
  `IHttpClientBuilder` so you can add resilience.
- **Targets** `net10.0` and `netstandard2.0` (so .NET 8/9 and .NET Framework 4.6.2+ apps work too).

## Install

```bash
dotnet add package TypstRender.Client
```

## Hello PDF in 60 seconds

**1. Start the rendering service.** No local Typst, no .NET SDK in the container:

```bash
docker run -p 8080:8080 ghcr.io/wetgi/typst-render-service:latest
curl localhost:8080/ready      # -> ok\ntypst 0.15.0
```

`/ready` answers `200` only once the Typst binary inside the image has actually run, so
it is the probe worth wiring into a health check. `/health` is the cheap liveness route.

**2. Write a template** at `templates/hello/main.typ`. The client serializes your data
object to `/data.json` in the bundle and tells Typst where to find it, so a template
reads its data like this:

```typst
#let data = json(sys.inputs.at("data-path", default: "data.json"))

#set page(paper: "a5", margin: 2cm)
= Hello #data.name
Rendered #data.date.
```

The `default:` keeps a local `typst compile --root . hello/main.typ` preview working
against a checked-in `templates/hello/data.json`.

**3. Copy the templates next to your binary**, in your `.csproj`. This matters: a
relative `TemplateRoot` resolves against the *process working directory*, which is not
your project folder under IIS or in a container.

```xml
<ItemGroup>
  <None Update="templates\**\*" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

**4. Register the client and render:**

```csharp
services.AddTypstRenderClient(o =>
{
    o.BaseAddress  = new Uri("http://localhost:8080");                     // the service
    o.TemplateRoot = Path.Combine(AppContext.BaseDirectory, "templates");  // the Typst --root
});

// ...
await typst.RenderToFileAsync("hello/main.typ", "hello.pdf", new { name = "World", date = "today" });
```

That is the whole loop. `hello.pdf` is on disk.

> Inside Docker Compose or Kubernetes the service is reachable by its service name
> (`http://typst-render:8080`), not `localhost` — that is the only line that changes
> between your machine and production.

## Rendering

`entry` is always relative to the template root, so `invoice/main.typ` addresses
`<root>/invoice/main.typ`. Pick the overload that matches where the PDF is going:

```csharp
public class InvoiceService(ITypstRenderClient typst)
{
    // In memory, when you need the bytes.
    public Task<byte[]> Bytes(InvoiceData data) =>
        typst.RenderAsync("invoice/main.typ", data);

    // Straight into another stream — an HTTP response, a blob upload. No buffering.
    public Task ToResponse(InvoiceData data, HttpResponse response) =>
        typst.RenderToAsync("invoice/main.typ", response.Body, data);

    // Straight to a file.
    public Task ToDisk(InvoiceData data, string path) =>
        typst.RenderToFileAsync("invoice/main.typ", path, data);
}
```

`RenderToStreamAsync` is also available when you need the stream itself — but you own
disposing it, and it holds a pooled connection until you do, so prefer `RenderToAsync`
when you are just copying.

## Full-control requests

For a per-call root, extra `--input` pairs, render-time assets, or an in-memory bundle,
use `TypstRenderRequest`:

```csharp
byte[] pdf = await typst.RenderAsync(new TypstRenderRequest
{
    TemplateRoot = "path/to/templates",   // overrides the configured default
    Entry        = "invoice/main.typ",
    Data         = invoiceData,
    Inputs       = { ["locale"] = "de" }, // extra typst --input pairs
    BundleMode   = BundleMode.Full,       // skip scanning, ship the whole root
    ExtraFiles   = { ["invoice/chart.svg"] = chartBytes }, // render-time assets
    // Files     = ...                    // in-memory bundle (embedded resources, ...)
});
```

`ExtraFiles` is for content generated at render time — charts, barcodes, signatures.
They ride along with the on-disk template (like `data.json` does) and the template
references them with ordinary paths — `image("chart.svg")` for the key above — while
the bundle scanner treats declared extra paths as present even when they are not on
disk. A key that matches a bundled file replaces it, so a template can keep a
checked-in placeholder for local previews.

A single `TypstRenderRequest` instance is read while the bundle is built, so give each
concurrent render its own. The client itself is safe to share.

## Inspecting the bundle

Both helpers are pure — no HTTP call is made:

```csharp
// Every folder (at any depth) under the root that has a main.typ entry.
IReadOnlyList<string> templates = typst.GetTemplates();     // ["docs/intro", "invoice", ...]

// Exactly which files an entry would upload — or the reason the scanner widened
// to the whole root. Throws with the reference chain if a file is missing on disk.
TemplateManifest manifest = typst.GetBundleManifest("invoice/main.typ");
```

### What the scanner can and cannot see

`BundleMode.Auto` reads your `.typ` files and follows string-literal references. It is
deliberately fail-safe: an `#import`/`#include` path it cannot resolve statically (a
variable, or a string concatenated with an expression) widens the bundle to the whole
root rather than risk a missing file — `TemplateManifest.FullFolderReason` tells you
which line did it. Comments, string contents and raw blocks are parsed properly, so a
documentation template that *shows* `#import "..."` inside backticks does not acquire it
as a dependency.

Two cases under-bundle rather than fail, and want `BundleMode.Full`:

- only the first path in a reader call is followed, so `bibliography(("a.bib", "b.bib"))`
  misses the second;
- a path built entirely by an expression — `image(paths.at(i))` — is invisible.

## Error handling

Everything that stops a render from producing a document surfaces as
`TypstRenderException`. The service's plain-text body is in both `Detail` **and** the
exception `Message`, so an ordinary `LogError(ex, ...)` already shows you the Typst
compiler output:

```csharp
try
{
    var pdf = await typst.RenderAsync("invoice/main.typ", data);
}
catch (TypstRenderException ex) when (ex.IsCompileError)      // HTTP 422
{
    logger.LogError("Typst could not compile {Entry}:\n{Stderr}", ex.Entry, ex.Detail);
}
catch (TypstRenderException ex) when (ex.IsTransportFailure)  // no response at all
{
    logger.LogError(ex, "Render service unreachable or too slow");
}
```

| `StatusCode` | Meaning                                                              |
| ------------ | -------------------------------------------------------------------- |
| `422`        | Typst compile error — `Detail` is the compiler stderr.               |
| `400`        | Malformed bundle, or a bad `input` pair.                             |
| `413`        | Uploaded bundle exceeded the service's max size.                     |
| `503`        | Service at capacity (concurrency limit).                             |
| `504`        | Render timed out inside the service.                                 |
| `500`        | The service could not run Typst — check its `Render:TypstBinaryPath`. |
| `0`          | No response: unreachable service, or the client's own `Timeout`.     |

Client-side mistakes fail before any HTTP call, which is the point of the scanner:
a missing `#import` target throws `FileNotFoundException` naming the chain of files
that led to it, and an entry outside the template root throws `ArgumentException`.

## Resilience

`AddTypstRenderClient` returns the `IHttpClientBuilder`, so the `503` and `504` the
service returns by design can be handled where they belong:

```csharp
services
    .AddTypstRenderClient(o => { /* ... */ })
    .AddStandardResilienceHandler();     // Microsoft.Extensions.Http.Resilience
```

## Options

Configure in code, or bind a configuration section:

```csharp
services.AddTypstRenderClient(builder.Configuration.GetSection("TypstRender"));
```

```json
{
  "TypstRender": {
    "BaseAddress": "http://typst-render:8080",
    "TemplateRoot": "templates",
    "Timeout": "00:01:00",
    "BundleMode": "Auto"
  }
}
```

| Option         | Default | Meaning                                                                             |
| -------------- | ------- | ----------------------------------------------------------------------------------- |
| `BaseAddress`  | —       | Base URL of the rendering service. Required; absolute http(s), no query or fragment. |
| `TemplateRoot` | —       | Default folder shipped as the Typst `--root`; entries are addressed relative to it. |
| `Timeout`      | 60s     | Overall request timeout.                                                            |
| `BundleMode`   | `Auto`  | `Auto` ships the entry's import closure; `Full` ships the whole root.               |

Options are validated the first time `ITypstRenderClient` is resolved, so a missing or
malformed `BaseAddress` fails with a message naming the setting rather than as an
opaque `HttpClient` error on your first render.

## Notes for .NET Framework / netstandard2.0 consumers

The package works on .NET Framework 4.6.2+, with two caveats in the snippets above:
`await using` needs `Stream : IAsyncDisposable` (use `using` instead), and primary
constructors need `<LangVersion>latest</LangVersion>`. `RenderToAsync` and
`RenderToFileAsync` work everywhere.

## License

MIT — see the [repository](https://github.com/wetgi/csharp-typst).
