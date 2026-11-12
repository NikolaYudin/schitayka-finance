#!/bin/sh
# Резервная копия базы SQLite из тома schitayka-data.
#
# Использование (из корня распакованного архива):
#   sudo sh scripts/backup.sh
#
# Контейнер останавливается на несколько секунд — чтобы копия была целостной
# (SQLite в режиме WAL). Результат: backups/schitayka-data-<дата>.tar.gz
set -eu

cd "$(dirname "$0")/.."
mkdir -p backups

STAMP=$(date +%Y%m%d-%H%M%S)
NAME="schitayka-data-$STAMP.tar.gz"
STARTED=0
TMP=$(mktemp -d)

resume() {
  if [ "$STARTED" = "1" ]; then
    docker start schitayka >/dev/null 2>&1 || true
  fi
}
cleanup() {
  resume
  rm -rf "$TMP"
}
trap cleanup EXIT INT TERM

if docker ps --format '{{.Names}}' 2>/dev/null | grep -qx 'schitayka'; then
  echo "[backup] останавливаю контейнер на несколько секунд..."
  docker stop schitayka >/dev/null
  STARTED=1
fi

echo "[backup] копирую /app/data из тома schitayka-data..."
docker cp schitayka:/app/data/. "$TMP/"

tar -czf "backups/$NAME" -C "$TMP" .

echo "[backup] готово: backups/$NAME ($(du -h "backups/$NAME" | cut -f1))"
