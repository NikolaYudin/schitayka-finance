# syntax=docker/dockerfile:1
#
# СчитайКа: multi-stage образ для Synology DSM / Xpenology (Container Manager).
# Контекст сборки — корень репозитория, поэтому собирать так:
#   docker build -f deploy/xpenology/Dockerfile -t schitayka:latest .
#
# Внутри: Node собирает SPA → .NET SDK публикует backend вместе с wwwroot →
# компактный runtime-образ ASP.NET Core 8.

ARG NODE_VERSION=20
ARG DOTNET_VERSION=8.0
ARG GIT_COMMIT=unknown

# ---------- 1. Сборка фронтенда ----------
FROM node:${NODE_VERSION}-bookworm-slim AS frontend
WORKDIR /src/frontend

# Сначала только манифесты — слой npm ci кэшируется между сборками.
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY frontend/ ./
# vite.config.ts пишет в ../backend/wwwroot — создаём каталог заранее.
RUN mkdir -p /src/backend \
 && npm run build

# ---------- 2. Сборка и публикация backend ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# Версия читается из VERSION в корне; копируем до restore, чтобы csproj её увидел.
COPY VERSION /src/VERSION
COPY backend/SchitayKa.Api.csproj backend/
RUN dotnet restore backend/SchitayKa.Api.csproj

COPY backend/ backend/
# Кладём собранный SPA в wwwroot; frontend/ в этом stage отсутствует,
# поэтому csproj не пытается запускать npm повторно.
COPY --from=frontend /src/backend/wwwroot/ backend/wwwroot/

ARG GIT_COMMIT=unknown

RUN dotnet publish backend/SchitayKa.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:SourceRevisionId=${GIT_COMMIT}

# ---------- 3. Runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    HOME=/home/app \
    TZ=Europe/Moscow

# curl — HEALTHCHECK; tzdata — TZ; tesseract — распознавание чеков/скриншотов офлайн.
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      curl tzdata tesseract-ocr tesseract-ocr-rus tesseract-ocr-eng \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish/ ./

RUN mkdir -p /app/data /home/app/.aspnet/DataProtection-Keys \
 && chown -R app:app /app /home/app

USER app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "SchitayKa.Api.dll"]
