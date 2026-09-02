# Build the ASP.NET rendering service with typst.
#
# Multi-arch: the build stage runs natively on the builder ($BUILDPLATFORM) and
# publishes portable IL — nothing in a framework-dependent publish is
# architecture-specific, and the entrypoint invokes the shared `dotnet` host
# rather than an apphost. Only the runtime stage is per-architecture, and the
# one native artefact it pulls (the typst CLI) is selected from $TARGETARCH.
ARG TYPST_VERSION=0.15.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the csproj graph first for better layer caching.
COPY Directory.Build.props ./
COPY src/TypstRender.Contracts/TypstRender.Contracts.csproj src/TypstRender.Contracts/
COPY src/TypstRender.Service/TypstRender.Service.csproj src/TypstRender.Service/
RUN dotnet restore src/TypstRender.Service/TypstRender.Service.csproj

COPY src/TypstRender.Contracts/ src/TypstRender.Contracts/
COPY src/TypstRender.Service/ src/TypstRender.Service/
RUN dotnet publish src/TypstRender.Service/TypstRender.Service.csproj \
        -c Release -o /app -p:UseAppHost=false

# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
ARG TYPST_VERSION
ARG TARGETARCH

# Latin-only libre font set (Liberation provides metric-compatible Arial/Times/Courier
# drop-ins; DejaVu covers broader Latin glyphs) plus the pinned Typst CLI on PATH.
# Typst's own defaults (Libertinus Serif / DejaVu Sans Mono / New Computer Modern)
# are embedded in the binary, so these packages only serve explicit font requests.
# icu-libs: the Alpine aspnet image omits ICU; keep it for culture-aware formatting.
RUN set -eu; \
    case "${TARGETARCH:-amd64}" in \
        amd64) TYPST_ARCH=x86_64 ;; \
        arm64) TYPST_ARCH=aarch64 ;; \
        *) echo "Unsupported TARGETARCH: ${TARGETARCH}" >&2; exit 1 ;; \
    esac; \
    apk add --no-cache \
        icu-libs \
        ca-certificates \
        ttf-liberation \
        ttf-dejavu; \
    apk add --no-cache --virtual .typst-build curl xz; \
    curl -fsSL "https://github.com/typst/typst/releases/download/v${TYPST_VERSION}/typst-${TYPST_ARCH}-unknown-linux-musl.tar.xz" \
        | tar -xJ --strip-components=1 -C /usr/local/bin "typst-${TYPST_ARCH}-unknown-linux-musl/typst"; \
    chmod +x /usr/local/bin/typst; \
    typst --version; \
    apk del .typst-build

COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# /ready answers 200 only once the typst binary has actually answered
# `--version`, so an image whose install is broken (missing, truncated, wrong
# architecture) is reported unhealthy instead of serving traffic and failing
# every render. busybox wget ships in the Alpine base — no extra package needed.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -q -O /dev/null http://127.0.0.1:8080/ready || exit 1

# Drop privileges: the service only ever writes to a per-render temp directory.
# $APP_UID is defined by the Microsoft base image (the non-root `app` user).
USER $APP_UID

ENTRYPOINT ["dotnet", "TypstRender.Service.dll"]
