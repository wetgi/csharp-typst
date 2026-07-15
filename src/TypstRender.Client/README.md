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
- **DI-first** — registers as a typed `HttpClient` via `IHttpClientFactory`.
- **Targets** `net10.0` and `netstandard2.0`.

## Install

```bash
dotnet add package TypstRender.Client
```

## Quick start

Start the rendering service — pull the published image (no local Typst needed):

```bash
docker run -p 8080:8080 ghcr.io/wetgi/typst-render-service:latest
```

Then register the client and point it at the service and your template folder:

```csharp
services.AddTypstRenderClient(o =>
{
    o.BaseAddress  = new Uri("http://typst-render:8080");
    o.TemplateRoot = "path/to/templates"; // becomes the Typst --root
});
```

Then inject `ITypstRenderClient` and render. The common case is one line — `entry` is
relative to the template root, and `data` is serialized to `data.json` at the bundle
root (templates read it with `#let data = json(sys.inputs.at("data-path"))`):

```csharp
public class InvoiceService(ITypstRenderClient typst)
{
    public async Task<byte[]> BuildInvoice(InvoiceData data) =>
        await typst.RenderAsync("invoice/main.typ", data);
}
```

Stream the response instead of buffering the whole PDF into a `byte[]`:

```csharp
await using Stream pdf = await typst.RenderToStreamAsync("invoice/main.typ", data);
await pdf.CopyToAsync(httpResponse.Body);
```

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
    ExtraFiles   = { ["generated/chart.svg"] = chartBytes }, // render-time assets
    // Files     = ...                    // in-memory bundle (embedded resources, ...)
});
```

`ExtraFiles` is for content generated at render time — charts, barcodes, signatures.
They ride along with the on-disk template (like `data.json` does) and the template
references them with ordinary paths, e.g. `image("/generated/chart.svg")`; the bundle
scanner treats declared extra paths as present even when they are not on disk.

## Inspecting the bundle

Both helpers are pure — no HTTP call is made:

```csharp
// Every folder (at any depth) under the root that has a main.typ entry.
IReadOnlyList<string> templates = typst.GetTemplates();

// Exactly which files an entry would upload — or the reason the scanner widened
// to the whole root. Throws with the reference chain if a file is missing on disk.
TemplateManifest manifest = typst.GetBundleManifest("invoice/main.typ");
```

## Error handling

A non-success response from the service surfaces as `TypstRenderException`, carrying the
HTTP status and the service's plain-text body — for a compile error (HTTP 422) that body
is the raw Typst `stderr`:

```csharp
try
{
    var pdf = await typst.RenderAsync("invoice/main.typ", data);
}
catch (TypstRenderException ex) when (ex.StatusCode == 422)
{
    logger.LogError("Typst compile failed:\n{Stderr}", ex.Detail);
}
```

| Status | Meaning                                                |
| ------ | ------------------------------------------------------ |
| `422`  | Typst compile error — `Detail` is the compiler stderr. |
| `400`  | Malformed bundle.                                      |
| `413`  | Uploaded bundle exceeded the service's max size.       |
| `503`  | Service at capacity (concurrency limit).               |
| `504`  | Render timed out.                                      |

## Options

| Option         | Default | Meaning                                                                             |
| -------------- | ------- | ----------------------------------------------------------------------------------- |
| `BaseAddress`  | —       | Base URL of the rendering service.                                                  |
| `TemplateRoot` | —       | Default folder shipped as the Typst `--root`; entries are addressed relative to it. |
| `Timeout`      | 60s     | Overall request timeout.                                                            |
| `BundleMode`   | `Auto`  | `Auto` ships the entry's import closure; `Full` ships the whole root.               |

## License

MIT — see the [repository](https://github.com/wetgi/csharp-typst).
