# Build context is the repository root:  docker build -f src/Kombin.Api/Dockerfile .

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore first so the layer is cached until a package reference changes.
COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Kombin.Api/Kombin.Api.csproj src/Kombin.Api/
RUN dotnet restore src/Kombin.Api/Kombin.Api.csproj

COPY src/Kombin.Api/ src/Kombin.Api/
RUN dotnet publish src/Kombin.Api/Kombin.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The base image ships an unprivileged "app" user; never run as root.
USER $APP_UID

HEALTHCHECK --interval=10s --timeout=3s --start-period=15s --retries=5 \
    CMD wget --quiet --output-document=/dev/null http://127.0.0.1:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "Kombin.Api.dll"]
