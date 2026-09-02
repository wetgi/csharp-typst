# Changelog

Notable changes to `TypstRender.Client` (the NuGet package) and
`ghcr.io/wetgi/typst-render-service` (the container image). Both are released from the
same `v*.*.*` tag. This project follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Client — fixed

- **The bundle scanner no longer drops files it should ship.** Comments and raw blocks
  are now stripped by a single string-aware pass instead of three independent regexes, so
  a `"/*"` or a `"//"` inside a string literal can no longer swallow the statements after
  it — which silently left imported modules and assets out of the uploaded bundle, and
  surfaced much later as a `422` from the service.
- **Prose is no longer mistaken for an import.** References are only matched in code
  position, so `Prices include "VAT"` in a template's text no longer fails the render
  with `FileNotFoundException: … resolves to 'invoice/VAT'`. Conversely, a raw block
  showing `#include <stdio.h>` no longer widens the bundle to the whole template root.
- **A dynamic `import` in code mode widens the bundle**, as `#import` already did. It was
  previously missed entirely, producing a silently incomplete bundle.
- **A concatenated import path (`import "/shared/" + name`) widens** instead of throwing.
  Reader calls with a concatenated path are skipped rather than failing the render.
- `RenderAsync(entry, cancellationToken)` **now binds the token to the token parameter.**
  It previously bound to `object? data`, serializing the `CancellationToken` into
  `data.json` and silently ignoring cancellation — and it compiled without a warning.
- **A `BaseAddress` carrying a query or fragment is rejected** instead of silently
  dropping the path prefix and producing a mystifying `404`. A relative or scheme-less
  address is rejected with a message naming the setting.
- **The injected `HttpClient` is no longer mutated.** Constructing a client over a shared
  or static `HttpClient` that had already sent a request used to throw.
- Cancellation now reaches the response-body read; `Files` keys are normalized like every
  other bundle path (a Windows caller no longer ships `invoice\main.typ`); an `entry`
  containing `..` is rejected instead of scanning outside the template root.

### Client — changed

- **`TypstRenderException` carries the compiler output in its `Message`**, plus `Entry`,
  `RequestUri`, `IsCompileError`, `IsTransportFailure` and an inner exception. A plain
  `logger.LogError(ex, …)` now shows the Typst stderr.
- **Unreachable service and client-side timeouts throw `TypstRenderException`** too
  (`StatusCode = 0`), rather than a raw `HttpRequestException` or a bare
  `TaskCanceledException` indistinguishable from the caller's own cancellation.
- **`AddTypstRenderClient` returns `IHttpClientBuilder`**, so a resilience handler can be
  attached to a client whose service returns `503`/`504` by design. Options are validated
  on first resolution, an `IConfiguration`-binding overload was added, and the extension
  moved to the `Microsoft.Extensions.DependencyInjection` namespace (no extra `using`).
- **The bundle cache no longer walks the whole template root on every render** — it
  re-stats only the bundled files and the directories that ship wholesale, and the cache
  is bounded so per-request `ExtraFiles` keys cannot grow it without limit.
- `RenderAsync(entry, data)` requires `data` explicitly; `RenderAsync(entry)` is
  unchanged. Source-breaking only for callers who wrote `RenderAsync(entry, data: null)`.

### Client — added

- `RenderToAsync(stream)` and `RenderToFileAsync(path)`: streaming renders that own the
  response lifetime, so neither can leak a pooled connection the way a forgotten
  `RenderToStreamAsync` disposal does.
- A constructor taking `TypstRenderClientOptions` directly, for console apps and tests
  with no DI container. `TemplateManifest` is now publicly constructible, so consumers
  can fake `ITypstRenderClient`.
- The package ships a `.snupkg` symbol package and a deterministic, SourceLink-able
  build, so consumers can step into the client.

### Service — fixed

- **A zip bomb can no longer fill the disk.** `Render:MaxExtractedBytes` (200 MB) and
  `Render:MaxBundleEntries` (2000) bound what a bundle may unpack to; the previous
  `MaxUploadBytes` capped only the compressed body.
- **A malformed archive returns `400`, not `500`.** An unsupported compression method, a
  CRC mismatch, a file/directory name clash or an over-long path used to escape as an
  unhandled exception — an empty `500` in production, a stack trace in development.
- **A client disconnect is no longer logged and returned as a `422` compile error**, which
  made the 422 rate meaningless. Cancellation now propagates, and a genuine timeout is
  distinguished from a caller giving up.
- **Typst warnings are no longer discarded.** A template requesting a font the image does
  not have rendered with a substituted typeface and left no trace anywhere; warnings are
  now logged.
- **A non-zero exit with no diagnostic returns `500`, not `422`** — that is the OOM
  killer, not a document the caller can fix. The exit code is logged.
- The entry-path confinement check now compares with a trailing separator; a killed Typst
  process is awaited before its workspace is deleted; log severity follows the status
  class, so alerting on `Warning+` sees timeouts and engine faults.

### Service — added

- **`GET /ready`**: answers `200` with the Typst version only once the binary has actually
  run, `503` otherwise. `GET /health` stays the cheap liveness route. The image's
  `HEALTHCHECK` probes `/ready`, so a broken install no longer reports itself healthy.
- **Startup validation of every `Render:` option**, with a message naming the key. A
  negative `TimeoutSeconds` previously orphaned a Typst process on every request.
- **`Render:PackagePath` / `Render:PackageCachePath`** for air-gapped deployments, plus
  documentation of the fact that `@preview` imports download and execute third-party code.
- Render workspaces left behind by a crashed process are swept at startup.
- `input` pairs are validated as `key=value` and a repeated `entry` is rejected, both with
  `400`, instead of reaching Typst and returning a `422` full of CLI usage text.

### Container — changed

- **Multi-arch: `linux/amd64` and `linux/arm64`.** The Typst binary is selected from
  `TARGETARCH`, so Apple Silicon and arm64 hosts get a native image instead of running
  under emulation — and `docker build` no longer fails on an arm64 host.
- **Runs as the base image's non-root `app` user**, with a `HEALTHCHECK`. Published images
  now carry an SBOM and `mode=max` build provenance.
- `ARG TYPST_VERSION` in the Dockerfile is the **single source of truth**; the install
  script derives from it and the workflow no longer carries a second copy.
- `Render:ShutdownTimeoutSeconds` now defaults to `45` (was `30`): an equal drain window
  and render timeout meant a render started just before SIGTERM was always aborted.

### Repository

- Added `Directory.Build.props`, `.editorconfig`, `CONTRIBUTING.md`, `SECURITY.md`, this
  changelog, and Dependabot for NuGet, Actions and Docker. Dependency audits report down
  to low severity, but only high and critical advisories fail a build — a low-severity
  advisory in a sample's transitive dependency should not block all work, which is what
  forced CI to skip the sample project before.
- `scripts/install-typst.sh` works on macOS (it only ever fetched a Linux-musl target, so
  the documented setup step could not be completed on a Mac) and now writes its
  `install-typst.sh.env` on every path — a runner that already had the pinned Typst on
  `PATH` exited before writing it, and CI's `source` of that file then failed the step
  before a single test ran.
- **CI builds and tests the whole solution** and packs the client as a release dry-run, so
  the `netstandard2.0` target is compiled before release rather than during it. Added a
  `permissions` block, concurrency cancellation and NuGet caching;
  `publish-nuget.yml` now declares `contents: read` explicitly.
- The sample references the client **project** instead of a released package, so its own
  build exercises the code in the repo. Consolidated three near-identical compose files
  into `docker-compose.yml` (build from source) and `docker-compose.ghcr.yml`.
- README corrected throughout: the sample is an HTTP API, not a console app that writes
  `./rendered/*.pdf`; quickstarts use `localhost`; the status-code and configuration
  tables match the code; added a "Deploying on Windows / IIS" section.

## [1.0.3]

Client: `BaseAddress` handling reworked so a configured base address always ends in a
slash and a path prefix survives request construction.

## [1.0.2] — image only

Published as a container image. **The NuGet package was never released**: `dotnet pack`
failed compiling the `netstandard2.0` target (`EndsWith(char, StringComparison)` does not
exist there) after CI, which built only the `net10.0` asset, had gone green. Fixed in
`1.0.3`; the CI change that prevents a repeat is in `Unreleased`.

## [1.0.1]

First widely usable release of the client and the service image.

[Unreleased]: https://github.com/wetgi/csharp-typst/compare/v1.0.3...HEAD
[1.0.3]: https://github.com/wetgi/csharp-typst/releases/tag/v1.0.3
[1.0.2]: https://github.com/wetgi/csharp-typst/releases/tag/v1.0.2
[1.0.1]: https://github.com/wetgi/csharp-typst/releases/tag/v1.0.1
