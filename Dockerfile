# syntax=docker/dockerfile:1

# ────────────────────────────────────────────────────────────────────────────────
# Stage 1 — build
#   Uses the full .NET SDK image to restore, build, and publish the service.
#   global.json is copied first so the 8.0.400 (latestFeature) SDK pin from
#   the repo is honoured inside the build stage.
#
#   Only the service project is restored and published; test projects are never
#   included in the image (they are excluded from the build context by
#   .dockerignore and are not referenced by these COPY / RUN steps).
# ────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy SDK pin and shared MSBuild props before project files so the restore
# layer is invalidated only when those root files change (not on every source
# edit).
COPY global.json .
COPY Directory.Build.props .

# Copy the service project file and restore as a distinct Docker layer.
# Restoring before copying the full source maximises cache reuse: a source-only
# change reuses this layer and skips the network-bound restore step.
COPY src/Vouchfx.Telemetry.Backend/Vouchfx.Telemetry.Backend.csproj \
     src/Vouchfx.Telemetry.Backend/

RUN dotnet restore src/Vouchfx.Telemetry.Backend/Vouchfx.Telemetry.Backend.csproj

# Copy the embedded-resource SQL file at the exact relative path the project
# expects: the .csproj uses Include="..\..\deploy\sql\bootstrap.sql", which
# from /src/src/Vouchfx.Telemetry.Backend/ resolves to /src/deploy/sql/bootstrap.sql.
# deploy/sql/ is un-ignored by .dockerignore so it reaches this COPY instruction.
COPY deploy/sql/ deploy/sql/

# Copy service source and publish a framework-dependent Release output.
COPY src/Vouchfx.Telemetry.Backend/ src/Vouchfx.Telemetry.Backend/

RUN dotnet publish src/Vouchfx.Telemetry.Backend/Vouchfx.Telemetry.Backend.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

# ────────────────────────────────────────────────────────────────────────────────
# Stage 2 — runtime
#   Uses the minimal ASP.NET Core 8 runtime image (~220 MB vs ~900 MB for the
#   SDK image) — no compilers, no SDK tooling in the shipped layer.
#
#   Security posture:
#     • Runs as the built-in non-root 'app' user (UID 64198) provided by the
#       mcr.microsoft.com/dotnet/aspnet base image — no useradd required.
#     • No secrets baked into the image. All secrets are injected at runtime
#       via Azure Container Apps secret references backed by Key Vault.
#     • TLS is terminated at the Container Apps platform ingress; the container
#       binds plain HTTP on port 8080 (a non-privileged port).
# ────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Listen on port 8080 (non-privileged; matches Container App ingress targetPort).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# No Dockerfile HEALTHCHECK — liveness and readiness are enforced by the
# Azure Container Apps platform probes defined in containerApp.bicep:
#   Liveness:  GET /healthz  (delay 30s, period 30s, timeout 5s, failureThreshold 3)
#   Readiness: GET /readyz   (delay 10s, period 10s, timeout 5s, failureThreshold 3)
#
# For local testing:  curl http://localhost:8080/healthz
#
# Note: wget is not present in the mcr.microsoft.com/dotnet/aspnet:8.0 base
# image, so a CMD-based HEALTHCHECK using wget would fail silently.  Removing
# it keeps the image lean and avoids misleading health state in local 'docker ps'.

# Drop privileges: run as the non-root 'app' user (UID 64198).
USER app

ENTRYPOINT ["dotnet", "Vouchfx.Telemetry.Backend.dll"]
