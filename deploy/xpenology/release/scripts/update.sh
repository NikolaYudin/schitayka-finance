#!/bin/sh
# Пересборка образа из ./app и пересоздание контейнера.
# Данные НЕ теряются: SQLite лежит в именованном томе schitayka-data.
#
# Использование (из корня распакованного архива):
#   sudo sh scripts/update.sh              # пересобрать из текущих файлов ./app
#   sudo sh scripts/update.sh --no-build   # только перезапуск (готовый образ)
set -eu

cd "$(dirname "$0")/.."

if docker compose version >/dev/null 2>&1; then
  DC="docker compose"
else
  DC="docker-compose"
fi

if [ "${1:-}" = "--no-build" ]; then
  echo "[SchitayKa] recreating container without build..."
  $DC up -d
else
  echo "[SchitayKa] building image from ./app and recreating container..."
  $DC up -d --build
fi

echo "[SchitayKa] done."
$DC ps
