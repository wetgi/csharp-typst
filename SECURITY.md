# Security

## Reporting a vulnerability

Please report security issues privately, not as a public issue: open a
[GitHub security advisory](https://github.com/wetgi/csharp-typst/security/advisories/new)
on this repository. Expect an acknowledgement within a few days.

## Threat model, in one page

Two things about this system are worth understanding before you deploy it.

### The service is unauthenticated by design

`TypstRender.Service` has no authentication, no authorization and no rate limiting. It is
built to sit on a private network reachable only by its caller. **Anything that can reach
`POST /render` can compile arbitrary Typst.** If it needs to be reachable from anywhere
else, put a reverse proxy that authenticates in front of it.

### A render compiles input you may not control

A request body is a zip that the service unpacks and hands to the Typst compiler. The
hardening that follows from that:

- **Uploads are bounded twice.** `Render:MaxUploadBytes` caps the compressed body
  (Kestrel rejects larger with `413`), and `Render:MaxExtractedBytes` plus
  `Render:MaxBundleEntries` cap what it may unpack to — a well-formed 20 MB zip of
  compressible data expands to tens of gigabytes otherwise.
- **Entries cannot escape the bundle root.** Every path is resolved and checked before it
  is written, and .NET's extraction never materializes a symlink (there is a test pinning
  that, because Typst *does* follow a symlink that sits inside its `--root`).
- **Typst is confined by `--root`** to the unpacked bundle, and each render gets a fresh
  temp directory that is deleted afterwards. Workspaces left by a crashed process are
  swept at startup.
- **Renders are bounded in time and number.** `Render:TimeoutSeconds` kills the process
  tree; `Render:MaxConcurrency` and `Render:QueueTimeoutSeconds` bound how much of the box
  one caller can occupy.
- **Compiler output is redacted and truncated** before it is returned, so a diagnostic
  cannot leak the internal temp path or become the payload.
- **The container runs as a non-root user** and writes only to a temp directory, so
  `--read-only --tmpfs /tmp` works and is recommended.

### Typst packages reach the network

A template that imports `@preview/...` makes Typst download and execute third-party code
from `packages.typst.org` at render time. Typst has no offline switch. If you do not need
packages, **block the container's egress** — nothing else in the service needs it. If you
do, pre-seed them and point `Render:PackagePath` at the directory. See the README's
configuration section.

## Supported versions

Fixes go onto the latest released minor version of the package and the `latest` image
tag. There are no long-term support branches.
