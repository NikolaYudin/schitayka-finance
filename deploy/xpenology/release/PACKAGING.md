# Сборка zip-дистрибутива для Container Manager

Документ для разработчика: как из репозитория получить архив, который пользователь
распаковывает в `/volume1/docker` и сразу создаёт проект в Container Manager.

Инструкция **для пользователя архива** — [`README.md`](README.md) (он же попадает в zip).

---

## 1. Одна команда

Windows (PowerShell 7):

```powershell
pwsh -File deploy/xpenology/release/build-release.ps1
```

Linux / macOS / CI:

```bash
./deploy/xpenology/release/build-release.sh
```

Результат: `dist/schitayka-<версия>-docker.zip` (~0,2 МБ, ~64 файла).
Скрипт печатает размер и SHA256.

---

## 2. Параметры упаковщика

| PowerShell | bash | Смысл |
|---|---|---|
| `-OutputDir <путь>` | `OUTPUT_DIR=<путь>` | Куда положить архив (по умолчанию `dist/`) |
| `-Version <x.y.z>` | `VERSION=<x.y.z>` | Версия (по умолчанию — файл `VERSION`) |
| `-RootName <имя>` | `ROOT_NAME=<имя>` | Папка внутри архива = имя проекта CM (по умолчанию `schitayka`) |
| `-NoEnv` | `--no-env` | Не класть `.env` с секретом (для публичных релизов) |
| `-Image` | `--image` | `docker-compose.yml` без сборки — готовый GHCR-образ |
| `-KeepStage` | `--keep-stage` | Оставить временную папку `.stage` для отладки |

Примеры:

```powershell
# публичный релиз без секретов
pwsh -File deploy/xpenology/release/build-release.ps1 -NoEnv

# вариант с готовым образом (после публикации в GHCR)
pwsh -File deploy/xpenology/release/build-release.ps1 -Image
```

---

## 3. Что попадает в архив

```
schitayka/
├── docker-compose.yml        ← release/docker-compose.yml (context: ./app)
├── docker-compose.image.yml  ← всегда, как справка/переключатель
├── .env                      ← генерируется: APP_PORT, JWT_KEY (48 байт), TZ, GIT_COMMIT
├── .env.example
├── VERSION
├── MANIFEST.txt              ← версия, коммит, дата, число файлов
├── README.md                 ← release/README.md (инструкция для пользователя)
├── app/
│   ├── Dockerfile            ← deploy/xpenology/Dockerfile (без изменений)
│   ├── .dockerignore         ← корневой .dockerignore
│   ├── VERSION
│   ├── backend/              ← без bin, obj, wwwroot, app.db*
│   └── frontend/             ← без node_modules, dist, tsconfig.tsbuildinfo
└── scripts/
    ├── init-env.sh
    ├── update.sh
    ├── backup.sh
    └── restore.sh
```

Почему `context: ./app`: Container Manager не принимает контекст сборки вне папки проекта.
Dockerfile при этом **не меняется** — он и так ожидает `backend/`, `frontend/`, `VERSION`
в корне контекста.

---

## 4. Что упаковщик проверяет автоматически

1. **Обязательные файлы в zip:** `docker-compose.yml`, `.env` (если не `-NoEnv`),
   `app/Dockerfile`, `app/VERSION`, `app/backend/SchitayKa.Api.csproj`,
   `app/backend/Data/AppDbContext.cs`, `app/backend/Data/AppDbContextFactory.cs`,
   `app/frontend/package.json`, `app/frontend/package-lock.json`,
   `scripts/update.sh`, `README.md`.
2. **LF в скриптах** — CRLF ломает `#!/bin/sh` на DSM.
3. **Исключения по полным путям** — нельзя исключать по имени `data`:
   на Windows это регистронезависимо вырежет `backend/Data` с исходниками EF-контекста.

Ручные проверки перед выпуском (выполняются на ПК):

```powershell
# 1. Распаковать в чистую папку и собрать backend
Expand-Archive dist/schitayka-<версия>-docker.zip $env:TEMP\ziptest
cd $env:TEMP\ziptest\schitayka\app
dotnet build backend/SchitayKa.Api.csproj

# 2. Синтаксис shell-скриптов
bash -n ../scripts/update.sh   # и остальные три

# 3. Валидность YAML
python -c "import yaml; yaml.safe_load(open('../docker-compose.yml', encoding='utf-8'))"

# 4. В архиве НЕТ мусора
#    app.db, node_modules, bin, obj, wwwroot
```

На NAS после распаковки:

```bash
cd /volume1/docker/schitayka
docker compose config          # проверка интерполяции .env
sudo sh scripts/update.sh      # первая сборка и запуск
curl http://127.0.0.1:5023/api/health
```

---

## 5. Выпуск новой версии

1. Поднять `VERSION` в корне репозитория.
2. Обновить `README.md` в `release/` (если менялись шаги/скрипты).
3. Собрать архив: `pwsh -File deploy/xpenology/release/build-release.ps1`.
4. Приложить zip к **GitHub Release** вместе с SHA256 из вывода скрипта.
5. Проверить установку на чистой папке NAS (или в тестовом проекте CM).

Обновление у пользователя: заменить `app/`, `docker-compose.yml` и `scripts/`,
**сохранив свой `.env`**, затем «Сборка → Запуск» в Container Manager
(или `sh scripts/update.sh`). Том `schitayka-data` не затрагивается.

---

## 6. Ограничения

- **Слабый NAS** (2 ГБ RAM) может не собрать образ — используйте `-Image` после
  публикации образа в GHCR (PLAN.md, B8) либо соберите на ПК и перенесите
  `docker save` / `docker load`.
- **ARM-модели** — только готовый образ.
- **Секрет в `.env`** — архив с `-NoEnv` обязателен для публикации; личный архив
  с ключом нельзя выкладывать в открытый доступ.
- **`+x` не сохраняется в zip** — скрипты запускаются через `sh`, а не `./`.
