# ── ARKANA GATEWAY — Dockerfile ──
# Multi-stage build: SDK → publish → runtime

# ============================================================
# Stage 1: Build & Publish
# ============================================================
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/sdk:10.0@sha256:60a2b2230a0d052bc54c0d453e97e331219ed503c5411470ee226859420e693c AS build
WORKDIR /src

# Copy project files for layer caching
COPY Directory.Build.props .
COPY Arkana.slnx .
COPY src/Arkana.Domain/*.csproj src/Arkana.Domain/
COPY src/Arkana.Application/*.csproj src/Arkana.Application/
COPY src/Arkana.Infrastructure/*.csproj src/Arkana.Infrastructure/
COPY src/Arkana.Gateway.Api/*.csproj src/Arkana.Gateway.Api/
COPY src/Arkana.ServiceDefaults/*.csproj src/Arkana.ServiceDefaults/
COPY src/Arkana.AppHost/*.csproj src/Arkana.AppHost/
COPY tests/Arkana.Domain.Tests/*.csproj tests/Arkana.Domain.Tests/
COPY tests/Arkana.Application.Tests/*.csproj tests/Arkana.Application.Tests/
COPY tests/Arkana.Infrastructure.Tests/*.csproj tests/Arkana.Infrastructure.Tests/
COPY tests/Arkana.Gateway.Api.Tests/*.csproj tests/Arkana.Gateway.Api.Tests/
COPY tests/Arkana.ServiceDefaults.Tests/*.csproj tests/Arkana.ServiceDefaults.Tests/
COPY tools/Arkana.Setup/*.csproj tools/Arkana.Setup/

# Restore the entire solution so all projects can build
RUN dotnet restore Arkana.slnx

# Copy all source
COPY . .

# Publish the Gateway API project
# Note: --no-restore omitted deliberately — it skips static web asset resolution
# which is needed for Blazor's blazor.server.js to be included in output.
RUN dotnet publish src/Arkana.Gateway.Api/Arkana.Gateway.Api.csproj \
    -c Release \
    -o /app/publish

# ============================================================
# Stage 2: Runtime
# ============================================================
FROM --platform=linux/amd64 mcr.microsoft.com/dotnet/aspnet:10.0@sha256:900c2dd83cc0cef53db0aaf786f12fe766ee075b6334750a664c9e77e7a7c0c5 AS runtime
WORKDIR /app

# The pinned ASP.NET runtime is Ubuntu 24.04 and already includes bash.
# Avoiding apt here keeps the production image independent of mutable package indexes.

# Copy published output
COPY --from=build /app/publish .

# Expose the gateway port
EXPOSE 5011

# Health check: validate the unauthenticated HTTP health endpoint, not only TCP accept.
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/5011; printf "GET /health HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n" >&3; IFS= read -r status <&3; case "$status" in "HTTP/1.0 200 "*|"HTTP/1.1 200 "*) exit 0;; *) exit 1;; esac'

ENTRYPOINT ["dotnet", "Arkana.Gateway.Api.dll"]
