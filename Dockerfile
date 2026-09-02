# syntax=docker/dockerfile:1

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG VERSION=0.0.0-local
ARG VCS_REF=unknown
WORKDIR /src

COPY LICENSE .
COPY src/ChangeDetection.Core/ChangeDetection.Core.csproj src/ChangeDetection.Core/
COPY src/ChangeDetection.Shared/ChangeDetection.Shared.csproj src/ChangeDetection.Shared/
COPY src/ChangeDetection/ChangeDetection.Client/ChangeDetection.Client.csproj src/ChangeDetection/ChangeDetection.Client/
COPY src/ChangeDetection/ChangeDetection/ChangeDetection.csproj src/ChangeDetection/ChangeDetection/
RUN dotnet restore src/ChangeDetection/ChangeDetection/ChangeDetection.csproj
RUN dotnet workload restore src/ChangeDetection/ChangeDetection/ChangeDetection.csproj

COPY src src
RUN dotnet publish src/ChangeDetection/ChangeDetection/ChangeDetection.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:Version=${VERSION} \
    -p:SourceRevisionId=${VCS_REF} \
    -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble AS runtime
ARG VERSION=0.0.0-local
ARG VCS_REF=unknown

LABEL org.opencontainers.image.title="ChangeDetection" \
      org.opencontainers.image.description="Self-hosted natural-language website change monitoring" \
      org.opencontainers.image.source="https://github.com/adsamcik/changedetection" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${VCS_REF}" \
      org.opencontainers.image.licenses="MIT"

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0 \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    LiteDb__Path=/data/changedetection.db \
    Security__DataProtectionKeyRingPath=/keys

USER root
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /src/LICENSE /licenses/LICENSE

# Invoke the package-bundled driver directly because the slim runtime image
# intentionally does not carry PowerShell. This still installs the exact browser
# revision selected by Microsoft.Playwright in the project.
RUN ./.playwright/node/linux-*/node ./.playwright/package/cli.js install --with-deps chromium \
    && apt-get update \
    && apt-get install --yes --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir --parents /data /keys /app/backups \
    && chown --recursive ${APP_UID}:${APP_UID} /data /keys /app/backups /ms-playwright

USER ${APP_UID}
EXPOSE 8080
VOLUME ["/data", "/keys", "/app/backups"]

HEALTHCHECK --interval=15s --timeout=5s --start-period=30s --retries=4 \
    CMD curl --fail --silent --show-error http://127.0.0.1:8080/health || exit 1

STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "ChangeDetection.dll"]
