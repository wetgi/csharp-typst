# Contributing

Thanks for looking. This is a small repository with two shipped artifacts — a NuGet
package (`TypstRender.Client`) and a container image
(`ghcr.io/wetgi/typst-render-service`) — so most of the setup below exists to keep those
two halves honest with each other.

## Setup

You need the .NET SDK from [`global.json`](global.json) and, for anything that actually
compiles a document, the Typst CLI:

```bash
./scripts/install-typst.sh          # downloads into ./.typst/bin (git-ignored)
source ./scripts/install-typst.sh.env   # puts it on PATH for this shell
```

The pinned version lives in **one** place: `ARG TYPST_VERSION` in
`src/TypstRender.Service/Dockerfile`. The install script reads it from there, and the
container workflow does not carry a second copy — so bumping Typst is a one-line change
and local builds cannot drift from published images.

## Build and test

```bash
dotnet build TypstRender.slnx
dotnet test  TypstRender.slnx
```

Always the whole solution, not just the test projects. The client multi-targets
`net10.0;netstandard2.0` and a test project only ever pulls in the `net10.0` asset, so a
netstandard-only break stays invisible until `dotnet pack` — which is exactly how `1.0.2`
came to exist as a container image with no matching package on nuget.org. CI builds the
solution and packs the client on every run for the same reason.

The client tests need nothing but the SDK. The service tests host the app in-process and
shell out to `typst`, so they fail confusingly if you skipped the `source` line above.

## Working on the two halves

- **`src/TypstRender.Client`** is the published package. Treat its public API as a
  contract: XML docs on everything public (they ship in the package and drive
  consumers' IntelliSense), and no source-breaking change without a version bump.
- **`src/TypstRender.Service`** is the container. It parses wholly untrusted input, so
  a change to `BundleExtractor` or the `entry` handling wants a test alongside it.
- **`src/TypstRender.Contracts`** holds the wire constants. It is not published: the
  client compiles `RenderProtocol.cs` in directly (see the comment in its `.csproj`) so
  the package has no dependency on an unpublished assembly.
- **`samples/TypstRender.Sample`** references the client by **project**, not by package,
  so a client change is exercised by `dotnet build TypstRender.slnx`. Please keep it
  that way — the sample previously pinned a released version and silently stopped
  testing the code in the repo.

Shared build settings live in [`Directory.Build.props`](Directory.Build.props); project
files should only carry what is genuinely project-specific. `TreatWarningsAsErrors` is on
everywhere.

## Running it end to end

```bash
docker compose up --build -d                     # the rendering service on :8080
curl localhost:8080/ready                        # confirm typst is runnable inside it
dotnet run --project samples/TypstRender.Sample  # the sample API on :5080
curl -o invoice.pdf localhost:5080/render/invoice
```

Templates work with the Typst CLI directly too, because the bundle keeps the on-disk
layout:

```bash
cd samples/TypstRender.Sample/templates
typst watch --root . invoice/main.typ
```

## Pull requests

- Keep the change focused, and add a test for anything that was a bug.
- `dotnet build TypstRender.slnx && dotnet test TypstRender.slnx` green before pushing.
- Note anything user-visible in [`CHANGELOG.md`](CHANGELOG.md) under `Unreleased`.
- Security-relevant findings: see [`SECURITY.md`](SECURITY.md) rather than opening a
  public issue.

## Releasing

Tags drive both artifacts. Pushing `v1.2.3`:

- `publish-nuget.yml` runs the full suite, packs `TypstRender.Client` at `1.2.3` (plus
  the `.snupkg` symbol package) and pushes to nuget.org via OIDC trusted publishing;
- `publish-container.yml` builds and pushes `linux/amd64` + `linux/arm64` images tagged
  `1.2.3`, `1.2`, and `sha-<commit>`, with an SBOM and build provenance.

Move the `CHANGELOG.md` `Unreleased` section under the new version before tagging.
