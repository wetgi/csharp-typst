// Minimal doc-page template living two levels deep (docs/intro).
// Pairs with docs/sample-api to showcase several nested templates
// discovered under a single parent folder.

#import "/shared/styles.typ": muted, primary, section-title

#set page(paper: "a4", margin: 2.5cm)
#set text(size: 11pt)
#set par(justify: true)

// Give code blocks a light tint and inline code a subtle box so the
// C# sample below reads as code, not prose.
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

#text(size: 20pt, weight: "bold", fill: primary)[Introduction]

#v(1em)

#section-title[What is this?]

A small sample showing how the renderer discovers and compiles templates
that live in nested directories, not just at the top level.

#v(0.6em)

#section-title[How it works]

The template scanner walks the folder tree and exposes each `main.typ`
by its root-relative path, so `docs/intro` and `docs/sample-api`
are addressable independently.

#v(0.6em)

#section-title[The bundle scanner]

When you render a template, the client does not blindly upload the whole
template folder. In the default `BundleMode.Auto` it computes the entry's
*import closure* — the smallest set of files the render actually needs — by
walking references statically:

#list(
  [The entry's own directory subtree (everything beside and below `main.typ`).],
  [The conventional `fonts/` directory at the root, if present.],
  [Every file reached through a string-literal `#import` / `#include`,
    such as `#import "/shared/styles.typ"`.],
  [Every asset pulled in by a reader with a literal path — `image(...)`,
    `read(...)`, `json(...)`, `csv(...)`, `yaml(...)`, and friends.],
)

#v(0.4em)

So shared modules outside the entry's folder ride along, while *sibling*
templates stay home. The scanner is deliberately fail-safe: if it meets a
dynamic `#import`/`#include` whose path it cannot resolve at scan time, it
widens the bundle to the entire template root rather than risk a missing
file. `BundleMode.Full` skips the analysis and always uploads everything.

#v(0.4em)

All of this bundle selection happens in the client, on your machine, before
anything is uploaded. The rendering service only ever receives the files the
client chose to send and simply compiles them — it never sees the rest of
your template root.

#v(0.6em)

#section-title[Previewing the bundle: GetBundleManifest]

`GetBundleManifest` runs that same analysis *without making any request*, so
you can see exactly which files would be uploaded — useful for answering
"why is this asset missing?" or "why is my whole folder being sent?" before
paying for a round-trip.

```csharp
// Inspect what would be uploaded for the report template.
TemplateManifest manifest = client.GetBundleManifest("report/main.typ");

foreach (string path in manifest.Files)
    Console.WriteLine(path);          // root-relative, '/'-separated, sorted

if (manifest.IsFullFolder)
    Console.WriteLine($"Widened to whole root: {manifest.FullFolderReason}");

// Override the bundle mode, and declare files injected at render time
// (e.g. data.json or a chart generated on the fly) so references to them
// are tolerated even though they are not on disk yet.
var full = client.GetBundleManifest(
    entry: "report/main.typ",
    bundleMode: BundleMode.Full,
    extraFiles: new[] { "report/generated/chart.svg" });
```

#v(0.4em)

*Parameters*

#list(
  [`entry` --- the entry `.typ` file, relative to the configured template
    root (e.g. `report/main.typ`). A reference that does not exist on disk
    fails here, naming the chain of files that led to it.],
  [`bundleMode` *(optional)* --- overrides the client's configured
    `BundleMode` for this call; `Auto` scans the closure, `Full` takes the
    whole root.],
  [`extraFiles` *(optional)* --- root-relative paths the render would inject
    via `TypstRenderRequest.ExtraFiles`. References to them are tolerated
    when missing on disk; they are not themselves listed in the manifest,
    just like `data.json`.],
)

#v(0.4em)

*Returns* a `TemplateManifest` with `Files` (the paths that would be
bundled), `FullFolderReason` (non-null and explanatory when the bundle was
widened to the whole root), and the `IsFullFolder` convenience flag.

#v(2em)

#text(fill: muted)[Next: see docs/sample-api for the HTTP endpoints.]
