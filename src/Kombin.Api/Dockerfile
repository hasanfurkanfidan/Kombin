# Build context is the repository root:  docker build -f src/Kombin.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore first so the layer is cached until a package reference changes.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Kombin.Api/Kombin.Api.csproj src/Kombin.Api/
RUN dotnet restore src/Kombin.Api/Kombin.Api.csproj

COPY src/Kombin.Api/ src/Kombin.Api/
# Embedded into the assembly: the weekly-metrics job runs the same queries operators run by hand.
COPY docs/metrics.sql docs/metrics.sql
RUN dotnet publish src/Kombin.Api/Kombin.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# The Alpine runtime image has no time zone database; quiet hours and the weekly schedule need Europe/Istanbul.
RUN apk add --no-cache tzdata

COPY --from=build /app/publish .

# The data-protection keys (the admin cookie and antiforgery tokens) live here in Production; the compose override mounts a named volume on it,
# which inherits this ownership, so a restart or an image update does not sign the founder out.
RUN mkdir /keys && chown $APP_UID /keys

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The base image ships an unprivileged "app" user; never run as root.
USER $APP_UID

HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=5 \
    CMD wget --quiet --output-document=/dev/null http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Kombin.Api.dll"]
