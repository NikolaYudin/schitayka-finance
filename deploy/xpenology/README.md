# СчитайКа на Synology / Xpenology

> **Полная пошаговая инструкция с нуля:** [INSTALL.md](INSTALL.md).

Готовая папка для сборки и запуска через **Container Manager (DSM 7.2+)**.
Внутри — multi-stage `Dockerfile` и `docker-compose.yml`: Node собирает фронтенд,
.NET SDK публикует backend, в runtime остаётся только ASP.NET Core 8 + SQLite.

```
deploy/xpenology/
├── Dockerfile          # multi-stage сборка (node → dotnet sdk → aspnet runtime)
├── docker-compose.yml  # сервис, порт, том с БД, healthcheck, лимиты логов
├── .env.example        # шаблон секретов и настроек
├── update.sh           # полное обновление (пересоздание контейнера, том сохраняется)
├── update-inplace.sh   # обновление кода без пересоздания контейнера
├── INSTALL.md          # полная пошаговая инструкция с нуля
├── release/            # zip-дистрибутив для Container Manager (см. ниже)
└── README.md
```

> **Самый простой путь — zip-дистрибутив.** Не нужно копировать весь репозиторий и
> возиться с контекстом сборки `../..`: архив распаковывается в `/volume1/docker`,
> и проект Container Manager создаётся прямо на папке `schitayka`.
> Сборка архива — [`release/PACKAGING.md`](release/PACKAGING.md), инструкция для
> пользователя (попадает в zip) — [`release/README.md`](release/README.md):
>
> ```powershell
> pwsh -File deploy/xpenology/release/build-release.ps1
> # → dist/schitayka-<версия>-docker.zip
> ```

> Контекст сборки — **корень репозитория** (`../..`). На NAS должен лежать весь проект,
> а не только папка `deploy/xpenology`.

---

## 1. Требования

- Xpenology / Synology DSM 7.2+ с **Container Manager**;
- архитектура x86_64 (стандарт для Xpenology);
- ~2–3 ГБ свободного места на время сборки (образ ~350 МБ + слои);
- интернет на NAS для загрузки базовых образов `node:20` и `mcr.microsoft.com/dotnet/*`;
- свободный порт **5023** (или свой — задаётся в `.env`).

---

## 2. Размещение проекта на NAS

Скопируйте весь репозиторий, например в:

```
/volume1/docker/schitayka/
├── backend/
├── frontend/
├── deploy/xpenology/   ← этот файл и compose
├── global.json
└── ...
```

Можно через File Station, SMB или `git clone`.

---

## 3. Настройка `.env`

В папке `deploy/xpenology`:

```bash
cp .env.example .env
```

Откройте `.env` и обязательно задайте `JWT_KEY` — случайную строку длиной **не менее 32 символов**:

```bash
openssl rand -base64 48
```

Пример `.env`:

```dotenv
APP_PORT=5023
JWT_KEY=вставьте_сюда_сгенерированную_строку
TZ=Europe/Moscow
SEED_DEMO=false
```

> Пока `JWT_KEY` пуст, `docker compose up` завершится ошибкой — это защита от запуска
> с известным/пустым ключом подписи.

- `SEED_DEMO=false` — production: демо-аккаунта `demo@schitayka.ru / demo123` нет.
  При первом входе создайте аккаунт во вкладке «Регистрация».
- `SEED_DEMO=true` — если хотите сначала посмотреть приложение на демо-данных.

> `.env` не коммитится в git (корневой `.gitignore`).

---

## 4. Запуск

### Вариант A. Container Manager (UI)

1. **Container Manager → Проект → Создать**.
2. Имя проекта: `schitayka`.
3. Путь: `/volume1/docker/schitayka/deploy/xpenology` — папка, где лежит `docker-compose.yml`.
4. Источник: используйте существующий `docker-compose.yml` (или загрузите его из этой папки).
5. Запустите сборку и дождитесь статуса **Running / Healthy**.
6. Откройте `http://<IP-НАС>:5023`.

Если мастер не даёт выбрать внешний контекст `../..`, используйте вариант B — он надёжнее.

### Вариант B. SSH (рекомендуется)

```bash
sudo -i
cd /volume1/docker/schitayka/deploy/xpenology
cp -n .env.example .env
vi .env                  # заполнить JWT_KEY
docker compose up -d --build
```

Проверка:

```bash
docker compose ps
docker logs -f schitayka
curl http://127.0.0.1:5023/api/health
```

Ожидаемый ответ: `{"status":"ok","app":"СчитайКа"}`.

> В старых DSM команда может называться `docker-compose` (v1) вместо `docker compose` (v2).

---

## 5. Первый вход

- Если `SEED_DEMO=false` (по умолчанию): откройте вкладку **«Регистрация»** и создайте
  собственный аккаунт. Стартовые категории создадутся автоматически.
- Если `SEED_DEMO=true`: можно войти как `demo@schitayka.ru` / `demo123`
  (подсказка появится на экране входа только при наличии демо-аккаунта).

---

## 6. Данные и резервные копии

SQLite-база лежит в именованном томе **`schitayka-data`** (`/app/data` внутри контейнера)
и переживает `docker compose down` / пересборку.

Путь на NAS:

```bash
sudo docker volume inspect schitayka-data
```

Бэкап тома:

```bash
sudo docker run --rm \
  -v schitayka-data:/data \
  -v /volume1/docker/schitayka:/backup \
  alpine tar czf /backup/schitayka-data-$(date +%F).tgz -C /data .
```

Восстановление (контейнер должен быть остановлен):

```bash
cd /volume1/docker/schitayka/deploy/xpenology
sudo docker compose down
sudo docker run --rm \
  -v schitayka-data:/data \
  -v /volume1/docker/schitayka:/backup \
  alpine sh -c "rm -rf /data/* && tar xzf /backup/schitayka-data-2026-11-10.tgz -C /data"
sudo docker compose up -d
```

Не заменяйте именованный том на bind-mount без `chown 1654:1654` — контейнер работает
под непривилегированным пользователем `app` (UID 1654).

---

## 7. Версия и обновление

Приложение показывает текущую версию и коммит в **Настройки → Версия и обновления**.
Там же — кнопка проверки последнего коммита в GitHub-репозитории
(`Update:RepoUrl`, по умолчанию `https://github.com/NikolaYudin/schitayka-finance`).
Если доступно обновление, в шапке появляется метка «Обновление», а в меню —
точка у «Настроек».

Проверка выполняется автоматически при входе и кэшируется на сервере на 10 минут.
Без `GITHUB_TOKEN` GitHub разрешает 60 запросов в час — для одного NAS этого достаточно;
токен можно задать в `.env`, если проверка упирается в rate limit.

> Приложение само не запускает `git`/`docker`: контейнер не имеет доступа к Docker-сокету.
> Это осознанно — обновление запускается командой на NAS, которую показывает карточка
> «Версия и обновления».

### 7.1. Обновить код без пересоздания контейнера

Рекомендуемый способ для обычных обновлений:

```bash
cd /volume1/docker/schitayka
git pull --ff-only
sudo sh deploy/xpenology/update-inplace.sh --no-pull
```

Что делает скрипт:

1. собирает новый образ `schitayka:latest`;
2. останавливает контейнер `schitayka`;
3. копирует новый код **внутрь существующего контейнера** (ID, имя, порты, тома сохраняются);
4. запускает контейнер снова.

Том `schitayka-data` не затрагивается — база и настройки остаются на месте.
Короткий простой (несколько секунд) неизбежен: приложение перезапускается.

Если `git` на NAS не установлен: замените файлы проекта вручную (ZIP/File Station)
и выполните:

```bash
sudo sh /volume1/docker/schitayka/deploy/xpenology/update-inplace.sh --no-pull
```

> Если вы дорабатывали проект локально, `git pull --ff-only` остановится на конфликте.
> Закоммитьте или спрячьте изменения (`git stash`), либо замените файлы вручную и
> запустите скрипт с `--no-pull`.

Скрипт использует имена `schitayka` (контейнер) и `schitayka:latest` (образ) из
`docker-compose.yml`. Если вы их меняли — поправьте скрипт.

### 7.2. Полное обновление (если менялись compose/env/базовый образ)

```bash
cd /volume1/docker/schitayka
git pull --ff-only
sudo sh deploy/xpenology/update.sh
```

`update.sh` выполняет `docker compose up -d --build`: контейнер пересоздаётся из нового
образа, но том `schitayka-data` сохраняется. Этот вариант нужен, если:

- менялись `docker-compose.yml`, `.env`, порты или переменные окружения;
- обновился базовый образ .NET/Node;
- новая версия требует новых системных пакетов внутри контейнера.

### 7.3. Версия проекта

Версия хранится в файле [`VERSION`](../../VERSION) в корне репозитория. При сборке она
попадает в сборку backend; коммит подставляется через `SourceRevisionId` (его передают
`update.sh`/`update-inplace.sh`). Чтобы выпустить новую версию — измените `VERSION`,
закоммитьте и пересоберите образ.

Если место на диске заканчивается:

```bash
docker image prune -f
docker builder prune -f
```

---

## 8. Сборка на ПК (если NAS слабый)

Собрать образ на компьютере с Docker:

```bash
docker build -f deploy/xpenology/Dockerfile -t schitayka:latest .
docker save schitayka:latest | gzip > schitayka-image.tgz
```

Загрузить `schitayka-image.tgz` на NAS и выполнить:

```bash
gunzip -c schitayka-image.tgz | sudo docker load
cd /volume1/docker/schitayka/deploy/xpenology
sudo docker compose up -d --no-build
```

---

## 9. Reverse Proxy и HTTPS

Приложение слушает HTTP 8080 внутри контейнера; наружу проброшен `APP_PORT` (5023).
Для доступа из интернета:

1. **Control Panel → Login Portal → Advanced → Reverse Proxy**:
   - источник: `https://finance.example.ru:443`;
   - назначение: `http://127.0.0.1:5023`.
2. В `docker-compose.yml` уже включён `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.
3. Не открывайте порт 5023 напрямую в интернет — только через reverse proxy и HTTPS.

**Важно:** регистрация в приложении открыта. Если доступ из интернета не нужен —
не пробрасывайте порт на роутере. Для публикации ограничьте доступ на уровне
reverse proxy (Allow/Deny по IP) или создайте аккаунты и закройте регистрацию
на уровне reverse proxy.

---

## 10. Диагностика

| Симптом | Что проверить |
|---|---|
| `JWT_KEY is not set` | `cp .env.example .env`, заполнить `JWT_KEY`, перезапустить |
| `bind: address already in use` | сменить `APP_PORT` в `.env` (например, 5024) |
| Контейнер `unhealthy` | `docker logs schitayka`; `curl http://127.0.0.1:5023/api/health` |
| `permission denied` на `/app/data` | не использовать bind-mount; вернуть именованный том |
| Container Manager не видит контекст | запускать из папки `deploy/xpenology` или использовать SSH |
| `update-inplace.sh` прервался | контейнер мог остаться остановленным: `sudo docker start schitayka`, затем повторите обновление; том с базой не пострадал |
| Долго собирается | первый build тянет SDK/Node; дальше слои кэшируются |
| Не хватает места | `docker system df`, `docker image prune -f`, `docker builder prune -f` |

---

## 11. Что внутри образа

- `node:20-bookworm-slim` — `npm ci` + `npm run build` (TypeScript + Vite);
- `mcr.microsoft.com/dotnet/sdk:8.0` — `dotnet publish -c Release`;
- `mcr.microsoft.com/dotnet/aspnet:8.0` — runtime, пользователь `app`, healthcheck;
- SPA лежит в `wwwroot`, backend отдаёт её сам — отдельный nginx не нужен;
- SQLite — в томе `schitayka-data`, миграции EF Core применяются при старте.
