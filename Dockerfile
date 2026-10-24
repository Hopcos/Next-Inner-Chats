# syntax=docker/dockerfile:1

# ───────────────────────────────────────────────
# Stage 1: frontend (Vue 3 + Vite) → /web/dist
# ───────────────────────────────────────────────
FROM node:22-bookworm-slim AS web-build
WORKDIR /web

# Install dependencies first (layer caching): only the manifests.
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY web/ ./
RUN npm run build

# ───────────────────────────────────────────────
# Stage 2: backend (.NET) → /app/publish
# ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
WORKDIR /src

# Restore first (layer caching): copy only props + project files.
COPY Directory.Build.props ./
COPY src/NextChats.Core/NextChats.Core.csproj src/NextChats.Core/
COPY src/NextChats.Infrastructure/NextChats.Infrastructure.csproj src/NextChats.Infrastructure/
COPY src/NextChats.Api/NextChats.Api.csproj src/NextChats.Api/
RUN dotnet restore src/NextChats.Api/NextChats.Api.csproj

COPY src/ src/
RUN dotnet publish src/NextChats.Api/NextChats.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:DebugType=None

# ───────────────────────────────────────────────
# Optional: demo MCP server (docker compose --profile demo up)
# ───────────────────────────────────────────────
FROM api-build AS demo-build
COPY samples/ samples/
RUN dotnet publish samples/McpDemoServer/McpDemoServer.csproj \
    -c Release -o /demo/publish /p:UseAppHost=false /p:DebugType=None

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS demo
WORKDIR /app
COPY --from=demo-build /demo/publish .
ENV ASPNETCORE_URLS=http://+:5300
EXPOSE 5300
USER $APP_UID
ENTRYPOINT ["dotnet", "McpDemoServer.dll"]

# ───────────────────────────────────────────────
# Runtime stage (default target)
# ───────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

COPY --from=api-build /app/publish .
# The API serves the SPA from wwwroot (UseStaticFiles + index.html fallback).
COPY --from=web-build /web/dist ./wwwroot

# Writable state lives in these directories (mount volumes on them):
#   data    SQLite database (relative path "data/nextchats.db")
#   uploads chat image attachments
#   logs    Serilog rolling files
# Secrets (Security__JwtKey / Security__EncryptionKey) come from the environment
# at runtime and are never baked into the image.
RUN mkdir -p /app/data /app/uploads /app/logs && chown -R $APP_UID:$APP_UID /app/data /app/uploads /app/logs

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

# Run as the non-root user shipped with the .NET base image.
USER $APP_UID

# The aspnet image has no curl/wget; probe /health with bash's /dev/tcp instead.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3 && head -n1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "NextChats.Api.dll"]
