# syntax=docker/dockerfile:1
#
# Build:
#   Local dev (uses ./packages populated by docker/build-local.ps1 from the local-nuget feed):
#     docker build -f docker/Dockerfile -t karmasis/minivault:dev .
#   CI (private feed, no local nupkgs baked in):
#     docker build -f docker/Dockerfile --build-arg NUGET_CONFIG=nuget-dev.config -t karmasis/minivault:ci .
#
# The repo's own nuget.config points at "..\..\local-nuget", a path outside the build
# context that only exists on a developer's machine, so it cannot be used inside the
# container. docker/nuget.docker.config is used instead; see that file for details.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG NUGET_CONFIG=docker/nuget.docker.config
WORKDIR /src

# Layer 1: files that change rarely, so `dotnet restore` below is cached until a
# .csproj or the solution/SDK pinning actually changes.
COPY global.json Directory.Build.props Karmasis.MiniVault.sln ./
COPY src/Karmasis.MiniVault.Server/Karmasis.MiniVault.Server.csproj src/Karmasis.MiniVault.Server/
COPY src/Karmasis.MiniVault.Contracts/Karmasis.MiniVault.Contracts.csproj src/Karmasis.MiniVault.Contracts/
COPY src/Karmasis.MiniVault.Client/Karmasis.MiniVault.Client.csproj src/Karmasis.MiniVault.Client/
COPY src/Karmasis.MiniVault.Client.DependencyInjection/Karmasis.MiniVault.Client.DependencyInjection.csproj src/Karmasis.MiniVault.Client.DependencyInjection/

# Local nupkg feed (docker/build-local.ps1 populates this from D:\Karmasis\local-nuget
# before `docker build` runs; empty in CI, which overrides NUGET_CONFIG instead).
COPY packages/ packages/
COPY ${NUGET_CONFIG} ./nuget.docker.config

RUN dotnet restore src/Karmasis.MiniVault.Server/Karmasis.MiniVault.Server.csproj --configfile ./nuget.docker.config

# Layer 2: the rest of the source, then publish without re-restoring.
COPY src/ src/
RUN dotnet publish src/Karmasis.MiniVault.Server -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ENV ASPNETCORE_ENVIRONMENT=Production
# The aspnet base image sets ASPNETCORE_HTTP_PORTS=8080, which would make Kestrel add a plain-HTTP listener.
# MiniVault listens only on Tls:Url over HTTPS, so both hints are cleared here; leaving them set would only
# produce a startup warning and a port nothing serves.
ENV ASPNETCORE_HTTP_PORTS=
ENV ASPNETCORE_URLS=
WORKDIR /app

# curl is needed by HEALTHCHECK below; installed as root before dropping privileges.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# The aspnet base image already ships a non-root "app" user (uid 1654).
COPY --from=build --chown=app:app /app .

EXPOSE 8200
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
    CMD curl -kfsS https://localhost:8200/v1/health || exit 1

USER app
ENTRYPOINT ["dotnet", "minivault.dll"]
