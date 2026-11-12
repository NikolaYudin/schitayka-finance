#!/usr/bin/env bash
# Собирает zip-дистрибутив СчитайКа для Synology / Xpenology (Container Manager).
# Аналог build-release.ps1 для Linux/macOS/CI.
#
# Использование:
#   ./build-release.sh                  # архив с .env (личный)
#   ./build-release.sh --no-env         # без секрета (публичный релиз)
#   ./build-release.sh --image          # compose без сборки (готовый GHCR-образ)
#   OUTPUT_DIR=/tmp ./build-release.sh
set -euo pipefail

RELEASE_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$RELEASE_DIR/../../.." && pwd)"
OUTPUT_DIR="${OUTPUT_DIR:-$REPO_ROOT/dist}"
VERSION="${VERSION:-$(tr -d '[:space:]' < "$REPO_ROOT/VERSION")}"
ROOT_NAME="${ROOT_NAME:-schitayka}"
NO_ENV=0
IMAGE=0
KEEP_STAGE=0

for arg in "$@"; do
  case "$arg" in
    --no-env)     NO_ENV=1 ;;
    --image)      IMAGE=1 ;;
    --keep-stage) KEEP_STAGE=1 ;;
    --output=*)   OUTPUT_DIR="${arg#*=}" ;;
    *) echo "Неизвестный аргумент: $arg" >&2; exit 2 ;;
  esac
done

[ -n "$VERSION" ] || { echo "Не удалось прочитать VERSION" >&2; exit 1; }

STAGE_ROOT="$OUTPUT_DIR/.stage"
STAGE_APP="$STAGE_ROOT/$ROOT_NAME"
SUFFIX=""
[ "$IMAGE" = "1" ] && SUFFIX="-image"
ZIP_PATH="$OUTPUT_DIR/schitayka-$VERSION-docker$SUFFIX.zip"

copy_tree() {
  local src="$1" dst="$2"; shift 2
  mkdir -p "$dst"
  if command -v rsync >/dev/null 2>&1; then
    local excludes=()
    for e in "$@"; do excludes+=(--exclude "$e"); done
    rsync -a "${excludes[@]}" "$src/" "$dst/"
  else
    cp -a "$src/." "$dst/"
    for e in "$@"; do
      find "$dst" -name "$e" -prune -exec rm -rf {} + 2>/dev/null || true
    done
  fi
}

to_lf() {
  # Убирает CR из shell-скриптов: иначе на DSM будет «not found» / bad interpreter.
  if command -v sed >/dev/null 2>&1; then
    sed -i 's/\r$//' "$1"
  fi
}

get_commit() {
  if command -v git >/dev/null 2>&1; then
    git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null || echo unknown
  else
    echo unknown
  fi
}

echo "== СчитайКа: сборка дистрибутива =="
echo "Версия:   $VERSION"
echo "Репозито: $REPO_ROOT"
echo "Вывод:    $OUTPUT_DIR"

rm -rf "$STAGE_ROOT"
mkdir -p "$STAGE_APP/app"

echo "[1/6] backend..."
copy_tree "$REPO_ROOT/backend" "$STAGE_APP/app/backend" bin obj wwwroot
find "$STAGE_APP/app/backend" -name 'app.db*' -delete 2>/dev/null || true

echo "[2/6] frontend..."
copy_tree "$REPO_ROOT/frontend" "$STAGE_APP/app/frontend" node_modules dist tsconfig.tsbuildinfo

echo "[3/6] Dockerfile, VERSION, .dockerignore..."
cp "$REPO_ROOT/deploy/xpenology/Dockerfile" "$STAGE_APP/app/Dockerfile"
cp "$REPO_ROOT/VERSION" "$STAGE_APP/app/VERSION"
cp "$REPO_ROOT/VERSION" "$STAGE_APP/VERSION"
cp "$REPO_ROOT/.dockerignore" "$STAGE_APP/app/.dockerignore"
find "$STAGE_APP/app" \( -name '*.user' -o -name '*.suo' -o -name 'tsconfig.tsbuildinfo' \) -delete 2>/dev/null || true

echo "[4/6] compose, .env, скрипты..."
if [ "$IMAGE" = "1" ]; then
  cp "$RELEASE_DIR/docker-compose.image.yml" "$STAGE_APP/docker-compose.yml"
else
  cp "$RELEASE_DIR/docker-compose.yml" "$STAGE_APP/docker-compose.yml"
fi
cp "$RELEASE_DIR/docker-compose.image.yml" "$STAGE_APP/docker-compose.image.yml"
cp "$RELEASE_DIR/.env.example" "$STAGE_APP/.env.example"
cp "$RELEASE_DIR/README.md" "$STAGE_APP/README.md"

mkdir -p "$STAGE_APP/scripts"
for f in "$RELEASE_DIR"/scripts/*.sh; do
  cp "$f" "$STAGE_APP/scripts/"
  to_lf "$STAGE_APP/scripts/$(basename "$f")"
done

COMMIT="$(get_commit)"

if [ "$NO_ENV" = "1" ]; then
  echo "[5/6] .env не создаю (--no-env)"
else
  echo "[5/6] генерирую .env..."
  if command -v openssl >/dev/null 2>&1; then
    KEY="$(openssl rand -base64 48 | tr -d '\n')"
  else
    KEY="$(head -c 48 /dev/urandom | base64 | tr -d '\n')"
  fi
  cat > "$STAGE_APP/.env" <<EOF
# СчитайКа — настройки проекта Container Manager.
# Создан упаковщиком $(date '+%Y-%m-%d %H:%M'). Содержит секрет: не публикуйте.

APP_PORT=5023
JWT_KEY=$KEY
TZ=Europe/Moscow
SEED_DEMO=false
UPDATE_REPO_URL=https://github.com/NikolaYudin/schitayka-finance
GITHUB_TOKEN=
GIT_COMMIT=$COMMIT
EOF
  chmod 600 "$STAGE_APP/.env"
fi

echo "[6/6] MANIFEST и упаковка..."
APP_COUNT="$(find "$STAGE_APP/app" -type f | wc -l | tr -d ' ')"
APP_SIZE="$(du -sh "$STAGE_APP/app" | cut -f1)"
cat > "$STAGE_APP/MANIFEST.txt" <<EOF
СчитайКа — дистрибутив для Synology / Xpenology (Container Manager)
Версия:        $VERSION
Коммит:        $COMMIT
Собрано:       $(date '+%Y-%m-%d %H:%M:%S %z')
Режим:         $([ "$IMAGE" = "1" ] && echo 'готовый образ' || echo 'сборка из ./app')
Файлов в app/: $APP_COUNT
Размер app/:   $APP_SIZE
EOF

mkdir -p "$OUTPUT_DIR"
rm -f "$ZIP_PATH"
if command -v zip >/dev/null 2>&1; then
  ( cd "$STAGE_ROOT" && zip -qr "$ZIP_PATH" "$ROOT_NAME" )
else
  python3 - "$STAGE_ROOT" "$ZIP_PATH" "$ROOT_NAME" <<'PY'
import os, sys, zipfile
stage, out, root = sys.argv[1], sys.argv[2], sys.argv[3]
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    base = os.path.join(stage, root)
    for dirpath, _, files in os.walk(base):
        for f in files:
            full = os.path.join(dirpath, f)
            z.write(full, os.path.relpath(full, stage))
PY
fi

# Проверка обязательных файлов.
python3 - "$ZIP_PATH" "$ROOT_NAME" "$NO_ENV" <<'PY'
import sys, zipfile
path, root, no_env = sys.argv[1], sys.argv[2], sys.argv[3]
required = [
    f"{root}/docker-compose.yml", f"{root}/app/Dockerfile", f"{root}/app/VERSION",
    f"{root}/app/backend/SchitayKa.Api.csproj", f"{root}/app/backend/Data/AppDbContext.cs",
    f"{root}/app/backend/Data/AppDbContextFactory.cs", f"{root}/app/frontend/package.json",
    f"{root}/app/frontend/package-lock.json", f"{root}/scripts/update.sh", f"{root}/README.md",
]
if no_env != "1":
    required.append(f"{root}/.env")
with zipfile.ZipFile(path) as z:
    names = set(z.namelist())
missing = [r for r in required if r not in names]
if missing:
    sys.exit("В архиве отсутствуют: " + ", ".join(missing))
print(f"Проверка архива: OK, файлов {len(names)}")
PY

[ "$KEEP_STAGE" = "1" ] || rm -rf "$STAGE_ROOT"

echo
echo "== Готово =="
echo "Архив:  $ZIP_PATH"
echo "Размер: $(du -h "$ZIP_PATH" | cut -f1)"
if command -v sha256sum >/dev/null 2>&1; then
  echo "SHA256: $(sha256sum "$ZIP_PATH" | cut -d' ' -f1)"
fi
