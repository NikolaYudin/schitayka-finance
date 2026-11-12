#!/bin/sh
# Создаёт .env из .env.example и генерирует случайный JWT_KEY.
#
# Использование (из корня распакованного архива):
#   sh scripts/init-env.sh
#
# Если .env уже существует — ничего не меняет (ключ и настройки сохраняются).
set -eu

cd "$(dirname "$0")/.."

if [ -f .env ]; then
  echo "[env] .env уже существует — оставляю как есть."
  exit 0
fi

if [ ! -f .env.example ]; then
  echo "[env] ERROR: не найден .env.example" >&2
  exit 1
fi

if command -v openssl >/dev/null 2>&1; then
  KEY=$(openssl rand -base64 48 | tr -d '\n')
else
  KEY=$(head -c 48 /dev/urandom | base64 | tr -d '\n')
fi

# '|' безопасен: в base64 нет этого символа.
sed "s|^JWT_KEY=.*|JWT_KEY=$KEY|" .env.example > .env
chmod 600 .env 2>/dev/null || true

echo "[env] .env создан, JWT_KEY сгенерирован (48 байт)."
echo "[env] Порт: $(grep '^APP_PORT=' .env | cut -d= -f2)"
