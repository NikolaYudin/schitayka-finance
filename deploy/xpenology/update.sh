#!/bin/sh
# Обновление СчитайКа с пересозданием контейнера из нового образа.
# Данные НЕ теряются: SQLite лежит в именованном томе schitayka-data.
#
# Использование (на NAS, из любой папки):
#   sudo sh /volume1/docker/schitayka/deploy/xpenology/update.sh
#
# Этот вариант нужен, если менялись docker-compose.yml, .env или базовый образ.
# Для обычного обновления только кода используйте update-inplace.sh.
set -eu

cd "$(dirname "$0")/../.."

echo "[SchitayKa] git pull --ff-only"
git pull --ff-only

GIT_COMMIT=$(git rev-parse HEAD 2>/dev/null || echo unknown)
export GIT_COMMIT
echo "[SchitayKa] commit: $GIT_COMMIT"

cd deploy/xpenology

if docker compose version >/dev/null 2>&1; then
  DC="docker compose"
else
  DC="docker-compose"
fi

echo "[SchitayKa] building and recreating container (volume schitayka-data is preserved)..."
$DC up -d --build

echo "[SchitayKa] done."
$DC ps
