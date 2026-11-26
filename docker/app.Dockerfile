# syntax=docker/dockerfile:1
#
# Dashboard web app: ASP.NET Core (Blazor Server) + SQLite + git (for partial clones of audited repos).
# Build from the repo root:  docker build -f docker/app.Dockerfile -t devops-dashboard-app .

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Dashboard.Core/Dashboard.Core.csproj src/Dashboard.Core/
COPY src/Dashboard.Web/Dashboard.Web.csproj src/Dashboard.Web/
RUN dotnet restore src/Dashboard.Web/Dashboard.Web.csproj
COPY src/Dashboard.Core/ src/Dashboard.Core/
COPY src/Dashboard.Web/ src/Dashboard.Web/
RUN dotnet publish src/Dashboard.Web/Dashboard.Web.csproj -c Release -p:UseAppHost=false -o /app \
 && test -f /app/wwwroot/_framework/blazor.web.js || (echo "blazor.web.js missing from publish output" && ls -R /app/wwwroot && exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
# git: partial clones + worktrees of audited repos (never builds or runs repo code). curl: healthcheck.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git curl ca-certificates openssl \
 && rm -rf /var/lib/apt/lists/*
ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_HTTP_PORTS= \
    DATA_DIR=/data \
    CLONES_PATH=/clones \
    DASHBOARD_CONFIG=/app/config/dashboard.yaml \
    RUBRIC_PATH=/app/audit/rubric \
    DOTNET_NOLOGO=1
# Pre-create volume mount points owned by the app user so new named volumes inherit the ownership.
RUN mkdir -p /data /clones /app/config /app/audit && chown -R "$APP_UID:$APP_UID" /data /clones
COPY --from=build /app /app
COPY --chmod=755 docker/with-ca.sh /usr/local/bin/with-ca
COPY config/dashboard.yaml /app/config/dashboard.yaml
COPY audit/ /app/audit/
USER $APP_UID
WORKDIR /app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD curl -fsS http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["/usr/local/bin/with-ca", "dotnet", "/app/Dashboard.Web.dll"]
