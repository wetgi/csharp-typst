# Build the ASP.NET rendering service with typst.
ARG TYPST_VERSION=0.15.0

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore against the csproj graph first for better layer caching.
COPY src/TypstRender.Contracts/TypstRender.Contracts.csproj src/TypstRender.Contracts/
COPY src/TypstRender.Service/TypstRender.Service.csproj src/TypstRender.Service/
RUN dotnet restore src/TypstRender.Service/TypstRender.Service.csproj

COPY src/TypstRender.Contracts/ src/TypstRender.Contracts/
COPY src/TypstRender.Service/ src/TypstRender.Service/
RUN dotnet publish src/TypstRender.Service/TypstRender.Service.csproj -c Release -o /app

# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
ARG TYPST_VERSION

# Latin-only libre font set (Liberation provides metric-compatible Arial/Times/Courier
# drop-ins; DejaVu covers broader Latin glyphs) plus the pinned Typst CLI on PATH.
# Typst's own defaults (Libertinus Serif / DejaVu Sans Mono / New Computer Modern)
# are embedded in the binary, so these packages only serve explicit font requests.
# icu-libs: the Alpine aspnet image omits ICU; keep it for culture-aware formatting.
RUN apk add --no-cache \
        icu-libs \
        ca-certificates \
        ttf-liberation \
        ttf-dejavu \
    && apk add --no-cache --virtual .typst-build curl xz \
    && curl -fsSL "https://github.com/typst/typst/releases/download/v${TYPST_VERSION}/typst-x86_64-unknown-linux-musl.tar.xz" \
        | tar -xJ --strip-components=1 -C /usr/local/bin typst-x86_64-unknown-linux-musl/typst \
    && chmod +x /usr/local/bin/typst \
    && typst --version \
    && apk del .typst-build

COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TypstRender.Service.dll"]
