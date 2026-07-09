// Doc-page template living two levels deep (docs/sample-api).
// Documents the HTTP endpoints exposed by the sample project, so the
// rendered PDF doubles as the sample's API reference.

#import "/shared/styles.typ": muted, primary, section-title

#set page(paper: "a4", margin: 2.5cm)
#set text(size: 11pt)
#set par(justify: true)

// Match the code styling used on the Introduction page.
#show raw.where(block: true): it => block(
  fill: rgb("#f4f6fb"),
  inset: 10pt,
  radius: 4pt,
  width: 100%,
)[#it]
#show raw.where(block: false): it => box(
  fill: rgb("#eef1f7"),
  inset: (x: 3pt, y: 0pt),
  outset: (y: 3pt),
  radius: 2pt,
)[#it]

// A small heading for each endpoint: HTTP verb + path on one line.
#let endpoint(method, path) = {
  v(0.2em)
  text(weight: "bold", fill: primary)[#method] + h(0.6em) + raw(path)
  v(0.2em)
}

#text(size: 20pt, weight: "bold", fill: primary)[Sample API]

#v(1em)

The sample project wraps the `ITypstRenderClient` behind a tiny HTTP API.
Templates are plain folders of `.typ` files under the configured template
root; the endpoints below let you list them, render one to a PDF, and
preview the bundle that would be uploaded. Nested template names contain a
`/`, which you send percent-encoded as `%2F` (for example
`docs%2Fsample-api`).

#v(0.8em)

#section-title[List templates]

#endpoint("GET", "/templates")

Returns every directory under the template root that contains a `main.typ`
entry, at any depth, as root-relative `/`-separated names. Directories
without an entry (shared modules, `fonts/`) are skipped. Use a returned name
directly with the render and manifest endpoints.

```json
["docs/intro", "docs/sample-api", "invoice", "report"]
```

#v(0.6em)

#section-title[Render a template]

#endpoint("GET", "/render/{templateName}")

Renders the named template and streams back the PDF
(`application/pdf`). The service loads the template's `data.json` (if
present) as the render payload, and — when that data carries a `chart`
array — generates `generated/chart.svg` on the fly and injects it as an
extra file before rendering.

#list(
  [*200* --- the rendered PDF, as a file download.],
  [*404* --- unknown template; the body lists the available templates.],
  [*422* --- the template failed to compile (bad Typst or data).],
)

```text
GET /render/report
GET /render/docs%2Fsample-api    // nested name, '/' encoded as %2F
```

#v(0.6em)

#section-title[Preview the bundle]

#endpoint("GET", "/manifest/{templateName}")

Computes exactly which files the client would upload for the template
*without rendering* — the live counterpart to `GetBundleManifest`. The
sample declares the same render-time extra files the real render injects
(the `chart.svg` above), so the preview matches what would actually be
sent. Handy for answering "why is this asset missing?" or "why is my whole
folder being uploaded?" before paying for a round-trip.

```json
{
  "templateName": "report",
  "files": ["report/main.typ", "shared/styles.typ"],
  "isFullFolder": false,
  "fullFolderReason": null
}
```

When the scanner cannot resolve an import statically it widens the bundle to
the whole template root; then `isFullFolder` is `true` and
`fullFolderReason` explains why.

#v(1.4em)

#text(fill: muted)[See docs/intro for how the scanner and GetBundleManifest work.]
