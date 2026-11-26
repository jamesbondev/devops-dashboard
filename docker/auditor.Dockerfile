# syntax=docker/dockerfile:1
#
# Auditor service: ASP.NET Core + Microsoft.Copilot.SDK (in-process native runtime), plus the
# standalone Copilot CLI of the *same* version for the one-time `login`.
#
# Build (from the repo root):   docker build -f docker/auditor.Dockerfile -t dashboard-auditor .
# Run (mock engine):            docker run --rm -p 127.0.0.1:8081:8081 -e AUDITOR_TOKEN=... -v clones:/clones:ro dashboard-auditor
# One-time login (compose):     docker compose run --rm auditor login
#
# The SDK package pins the runtime version (build/Microsoft.Copilot.SDK.props -> CopilotCliVersion);
# its MSBuild targets download that runtime bundle and copy libcopilot_runtime.so + the `copilot`
# runtime wrapper into runtimes/linux-x64/native/. That wrapper only speaks the SDK protocol, so the
# full CLI (for `login`) is fetched separately from the same GitHub release and checked against the
# release's SHA256SUMS.txt that the SDK target already downloaded.

ARG DOTNET_VERSION=10.0

# ---------- restore + runtime bundle (cached until the project files change) ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS deps
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Dashboard.Core/Dashboard.Core.csproj src/Dashboard.Core/
COPY src/Dashboard.Auditor/Dashboard.Auditor.csproj src/Dashboard.Auditor/
RUN dotnet restore src/Dashboard.Auditor/Dashboard.Auditor.csproj -r linux-x64
# Run the SDK's download target on its own so the ~150 MB bundle lands in a cached layer.
RUN dotnet msbuild src/Dashboard.Auditor/Dashboard.Auditor.csproj -nologo -v:m \
        -t:_DownloadCopilotCli -p:Configuration=Release -p:RuntimeIdentifier=linux-x64 \
 && ls -d src/Dashboard.Auditor/obj/Release/net10.0/linux-x64/copilot-cli/*/linux-x64

# ---------- standalone CLI for `login` (same version as the SDK runtime) ----------
FROM deps AS cli
RUN set -eu; \
    bundle="$(ls -d src/Dashboard.Auditor/obj/Release/net10.0/linux-x64/copilot-cli/*/linux-x64 | head -n1)"; \
    version="$(basename "$(dirname "$bundle")")"; \
    echo "Copilot CLI version pinned by the SDK: $version"; \
    curl -fsSL --retry 3 -o /tmp/copilot-linux-x64.tar.gz \
        "https://github.com/github/copilot-cli/releases/download/v${version}/copilot-linux-x64.tar.gz"; \
    expected="$(awk '$2 == "copilot-linux-x64.tar.gz" || $2 == "*copilot-linux-x64.tar.gz" { print $1 }' "$bundle/SHA256SUMS.txt")"; \
    test -n "$expected"; \
    echo "$expected  /tmp/copilot-linux-x64.tar.gz" | sha256sum -c -; \
    mkdir -p /opt/copilot-cli; \
    tar -xzf /tmp/copilot-linux-x64.tar.gz -C /opt/copilot-cli; \
    rm /tmp/copilot-linux-x64.tar.gz; \
    chmod 755 /opt/copilot-cli/copilot; \
    /opt/copilot-cli/copilot --version

# ---------- build + publish ----------
FROM deps AS build
COPY src/Dashboard.Core/ src/Dashboard.Core/
COPY src/Dashboard.Auditor/ src/Dashboard.Auditor/
RUN dotnet publish src/Dashboard.Auditor/Dashboard.Auditor.csproj -nologo \
        -c Release -r linux-x64 --self-contained false --no-restore -p:UseAppHost=false -o /app \
 && test -f /app/runtimes/linux-x64/native/libcopilot_runtime.so \
 && test -x /app/runtimes/linux-x64/native/copilot \
 && du -sh /app /app/runtimes/linux-x64/native

# ---------- runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
ENV ASPNETCORE_URLS=http://0.0.0.0:8081 \
    ASPNETCORE_HTTP_PORTS= \
    AUDITOR_ENGINE=mock \
    AUDITOR_ALLOWED_ROOT=/clones \
    COPILOT_HOME=/copilot-home \
    COPILOT_AUTO_UPDATE=false \
    DOTNET_NOLOGO=1
# ca-certificates + openssl: trust extra corporate root CAs from /certs (docker/with-ca.sh).
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates openssl \
 && rm -rf /var/lib/apt/lists/*
# /copilot-home holds the login (mount a named volume there); /clones is mounted read-only.
RUN mkdir -p /copilot-home /clones && chown "$APP_UID:$APP_UID" /copilot-home && chmod 700 /copilot-home
COPY --from=cli /opt/copilot-cli /opt/copilot-cli
COPY --from=build /app /app
COPY --chmod=755 docker/with-ca.sh /usr/local/bin/with-ca
COPY --chmod=755 <<'EOF' /usr/local/bin/auditor-entrypoint
#!/bin/sh
# serve (default)      start the auditor HTTP service
# login [args]         one-time GitHub device-code login, stored in $COPILOT_HOME
# copilot [args]       run the bundled Copilot CLI (e.g. `copilot login --help`)
# anything else        exec as-is
set -eu
cli=/opt/copilot-cli/copilot
cmd="${1:-serve}"
[ "$#" -gt 0 ] && shift
case "$cmd" in
  serve)
    exec dotnet /app/Dashboard.Auditor.dll "$@" ;;
  login)
    mkdir -p "$COPILOT_HOME"
    echo "Signing in to GitHub Copilot (device code). Credentials are stored in $COPILOT_HOME." >&2
    exec "$cli" login --device-code "$@" ;;
  copilot)
    exec "$cli" "$@" ;;
  *)
    exec "$cmd" "$@" ;;
esac
EOF
USER $APP_UID
WORKDIR /app
EXPOSE 8081
ENTRYPOINT ["/usr/local/bin/with-ca", "/usr/local/bin/auditor-entrypoint"]
CMD ["serve"]
