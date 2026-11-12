#!/bin/sh
# Обновление кода БЕЗ пересоздания контейнера.
#
# Как работает:
#   1) git pull (или пропуск с --no-pull, если код уже заменили вручную);
#   2) собирается новый образ schitayka:latest;
#   3) контейнер останавливается, новый код копируется внутрь него;
#   4) контейнер запускается снова — ID/имя/порты/тома сохраняются.
#
# База данных в томе schitayka-data не затрагивается.
# Если менялись docker-compose.yml/.env/базовый образ — используйте update.sh.
#
# Использование:
#   sudo sh /volume1/docker/schitayka/deploy/xpenology/update-inplace.sh
#   sudo sh .../update-inplace.sh --no-pull     # если git pull уже сделан
#
# Если git на NAS не установлен: замените файлы проекта вручную и запустите
# скрипт с --no-pull.
set -eu

cd "$(dirname "$0")/../.."

if [ "${1:-}" != "--no-pull" ]; then
  echo "[SchitayKa] git pull --ff-only"
  git pull --ff-only
fi

GIT_COMMIT=$(git rev-parse HEAD 2>/dev/null || echo unknown)
export GIT_COMMIT
echo "[SchitayKa] commit: $GIT_COMMIT"

cd deploy/xpenology

if docker compose version >/dev/null 2>&1; then
  DC="docker compose"
else
  DC="docker-compose"
fi

echo "[SchitayKa] building new image..."
$DC build

TMP="schitayka-update-tmp"
docker rm -f "$TMP" >/dev/null 2>&1 || true
docker create --name "$TMP" schitayka:latest >/dev/null

cleanup() {
  docker rm -f "$TMP" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

ITEMS=$(docker run --rm --entrypoint sh schitayka:latest -c 'ls -A /app')
if [ -z "$ITEMS" ]; then
  echo "[SchitayKa] ERROR: /app в новом образе пуст" >&2
  exit 1
fi

DIRS=$(docker run --rm --entrypoint sh schitayka:latest -c 'for d in /app/*/; do basename "$d"; done')

echo "[SchitayKa] stopping container (data volume is preserved)..."
docker stop schitayka >/dev/null 2>&1 || true

echo "[SchitayKa] copying new code into the existing container..."
for item in $ITEMS; do
  if [ "$item" = "data" ]; then
    continue
  fi

  if printf '%s\n' "$DIRS" | grep -Fqx "$item"; then
    # Копируем содержимое каталога (wwwroot, runtimes), не создавая вложенный каталог.
    docker cp "$TMP:/app/$item/." "schitayka:/app/$item/"
  else
    docker cp "$TMP:/app/$item" "schitayka:/app/"
  fi
done

echo "[SchitayKa] starting container..."
docker start schitayka >/dev/null

echo "[SchitayKa] done: container was not recreated, data volume schitayka-data preserved."
docker ps --filter "name=^/schitayka$" --format "{{.Names}} {{.Status}}"
