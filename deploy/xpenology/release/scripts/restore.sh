#!/bin/sh
# Восстановление базы из архива, созданного scripts/backup.sh.
#
# Использование (из корня распакованного архива):
#   sudo sh scripts/restore.sh backups/schitayka-data-20261112-153000.tar.gz
#   sudo sh scripts/restore.sh backups/....tar.gz --yes    # без подтверждения
#
# ВНИМАНИЕ: текущие данные в томе schitayka-data будут заменены.
set -eu

cd "$(dirname "$0")/.."

FILE=${1:-}
CONFIRM=${2:-}

if [ -z "$FILE" ]; then
  echo "Использование: sudo sh scripts/restore.sh backups/<файл>.tar.gz [--yes]" >&2
  echo "Доступные копии:" >&2
  ls -1 backups 2>/dev/null || echo "  (папка backups пуста)" >&2
  exit 1
fi

if [ ! -f "$FILE" ]; then
  echo "[restore] ERROR: файл не найден: $FILE" >&2
  exit 1
fi

case "$FILE" in
  /*) ABS="$FILE" ;;
  *)  ABS="$(pwd)/$FILE" ;;
esac

if [ "$CONFIRM" != "--yes" ]; then
  echo "ВНИМАНИЕ: база в томе schitayka-data будет заменена содержимым:"
  echo "  $ABS"
  printf "Продолжить? [y/N] "
  read -r ANSWER
  case "$ANSWER" in
    y|Y|yes|YES) ;;
    *) echo "[restore] отменено."; exit 1 ;;
  esac
fi

echo "[restore] останавливаю контейнер..."
docker stop schitayka >/dev/null 2>&1 || true

echo "[restore] очищаю том и распаковываю копию..."
docker run --rm --user root \
  -v schitayka-data:/data \
  -v "$ABS:/restore.tar.gz:ro" \
  --entrypoint sh schitayka:latest \
  -c 'rm -rf /data/* /data/.[!.]* 2>/dev/null || true; tar xzf /restore.tar.gz -C /data; chown -R app:app /data'

echo "[restore] запускаю контейнер..."
docker start schitayka >/dev/null

echo "[restore] готово. Проверка: curl -fsS http://127.0.0.1:5023/api/health"
