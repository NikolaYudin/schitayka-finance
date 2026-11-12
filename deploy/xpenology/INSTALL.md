# Полная инструкция: развёртывание СчитайКа на Xpenology / Synology

Пошагово: от копирования проекта на NAS до первого входа, обновлений и резервных копий.
Рассчитано на **DSM 7.2+ с Container Manager** (Xpenology x86_64).

---

## 0. Что получится

- проект **Container Manager** с именем `schitayka`;
- веб-интерфейс: `http://<IP-НАС>:5023`;
- SQLite-база в именованном томе `schitayka-data` (переживает пересборку);
- backend сам раздаёт SPA — отдельный nginx не нужен;
- версия/коммит видны в **Настройки → Версия и обновления**;
- обновление кода — скриптом, без пересоздания контейнера и без потери базы;
- импорт операций из фото чека или скриншота банка (OCR внутри контейнера, без внешних сервисов);
- экспорт полного профиля в JSON для анализа ИИ-агентом.

---

## 1. Что понадобится

- Xpenology/Synology DSM 7.2+ с установленным **Container Manager**;
- администратор NAS;
- ~3 ГБ свободного места (сборка + образ);
- интернет на NAS (скачать `node:20` и `mcr.microsoft.com/dotnet/*`, позже — проверять GitHub);
- свободный порт **5023** (можно изменить в `.env`);
- желательно SSH: **Control Panel → Terminal & SNMP → Enable SSH**.

Проверить Docker на NAS:

```bash
sudo docker --version
sudo docker compose version    # в старых DSM: docker-compose --version
```

---

## 2. Скопировать проект на NAS

> **Быстрый путь без копирования репозитория.** Соберите zip-дистрибутив на ПК:
> `pwsh -File deploy/xpenology/release/build-release.ps1` → `dist/schitayka-<версия>-docker.zip`.
> Распакуйте его в `/volume1/docker` (получится `/volume1/docker/schitayka`) — и сразу
> создавайте проект в Container Manager. Контекст сборки `../..` больше не нужен,
> `.env` с ключом уже внутри. Подробности — [`release/PACKAGING.md`](release/PACKAGING.md)
> (сборка) и [`release/README.md`](release/README.md) (инструкция в архиве).
> Шаги 2–3 ниже нужны только при развёртывании из git-клона.

Нужен **весь репозиторий**, а не только папка `deploy/xpenology`.
Целевой путь ниже — `/volume1/docker/schitayka` (можно другой, команды поправьте).

### Важно про текущую папку

В текущей рабочей папке есть изменения, которые ещё не закоммичены (новый дизайн,
Docker, проверка версий, скрипты обновления). Если сейчас сделать `git clone`
из GitHub, вы получите **старую версию** без этих изменений.

Варианты:

1. **Проще:** скопировать текущую папку проекта целиком на NAS (File Station, SMB, `scp`).
2. **Для обновлений через git:** сначала закоммитить и запушить изменения в свой
   репозиторий/форк, затем клонировать его на NAS.

### Через File Station / SMB

Скопируйте папку проекта в:

```
/volume1/docker/schitayka/
├── backend/
├── frontend/
├── deploy/xpenology/
├── VERSION
├── global.json
└── ...
```

Проверка по SSH:

```bash
ls /volume1/docker/schitayka
ls /volume1/docker/schitayka/deploy/xpenology
```

Должны быть видны `Dockerfile`, `docker-compose.yml`, `update.sh`, `update-inplace.sh`, `VERSION`.

### Через git (если код уже запушен)

```bash
cd /volume1/docker
sudo git clone <URL-вашего-репозитория> schitayka
```

> `git pull` в скриптах обновления тянет **origin**. Если у вас форк — origin должен
> указывать на ваш репозиторий, а `UPDATE_REPO_URL` в `.env` — на репозиторий для
> проверки обновлений.

---

## 3. Настроить `.env`

```bash
cd /volume1/docker/schitayka/deploy/xpenology
cp .env.example .env
chmod 600 .env
```

Сгенерировать ключ:

```bash
openssl rand -base64 48
```

Открыть `.env`:

```bash
vi .env
```

Пример:

```dotenv
APP_PORT=5023
JWT_KEY=сюда_вставьте_сгенерированную_строку
TZ=Europe/Moscow
SEED_DEMO=false
UPDATE_REPO_URL=https://github.com/NikolaYudin/schitayka-finance
GITHUB_TOKEN=
```

Обязательно:

| Переменная | Зачем |
|---|---|
| `JWT_KEY` | подпись токенов, **не короче 32 символов**, уникальная |
| `APP_PORT` | внешний порт на NAS (по умолчанию 5023) |
| `SEED_DEMO` | `false` — без демо-аккаунта; `true` — создать demo для теста |
| `TZ` | часовой пояс контейнера |

Пока `JWT_KEY` пуст, `docker compose up` специально завершится ошибкой.

> `.env` не коммитится в git и не должен попадать в образ.

---

## 4. Собрать и запустить

### Вариант A. SSH (рекомендуется)

```bash
cd /volume1/docker/schitayka/deploy/xpenology
sudo docker compose up -d --build
```

Если команда `docker compose` не найдена:

```bash
sudo docker-compose up -d --build
```

Первый запуск:

- скачиваются базовые образы `node:20` и `mcr.microsoft.com/dotnet/*`;
- собирается фронтенд, затем backend;
- обычно 5–15 минут в зависимости от CPU.

Проверка:

```bash
sudo docker compose ps
sudo docker logs -f schitayka
curl http://127.0.0.1:5023/api/health
```

Ожидаемый ответ:

```json
{"status":"ok","app":"СчитайКа"}
```

`Ctrl+C` выходит из логов, контейнер продолжает работать.

### Вариант B. Container Manager (UI)

1. **Container Manager → Проект → Создать**.
2. Имя: `schitayka`.
3. Путь: `/volume1/docker/schitayka/deploy/xpenology`.
4. Источник: существующий `docker-compose.yml` из этой папки.
5. Запустить сборку, дождаться статуса **Running / Healthy**.

Если UI не принимает контекст `../..` или не видит `.env` — используйте вариант A.

---

## 5. Первый вход

Откройте:

```
http://<IP-НАС>:5023
```

- `SEED_DEMO=false` (по умолчанию): вкладка **«Регистрация»** → имя, email, пароль
  (минимум 6 символов), при необходимости код семьи.
- `SEED_DEMO=true`: можно войти как `demo@schitayka.ru` / `demo123`;
  подсказка появится на экране входа только при наличии демо-аккаунта.

После регистрации создаются стартовые категории. Дальше можно добавить счета,
лимиты и цели.

---

## 5.1. Импорт операций из чека или скриншота

В окне новой операции (кнопка «Операция» в шапке или FAB) есть блок импорта:

1. **«Скриншот или чек»** — выберите файл: фото бумажного чека или скриншот из банка
   (JPG, PNG, WebP, BMP, TIFF, до 10 МБ).
2. Приложение распознаёт текст (tesseract, русский + английский) и подставляет:
   сумму, дату, тип (доход/расход), продавца и категорию.
3. В карточке «Распознано» видно уверенность и предупреждения — проверьте поля.
4. Нажмите **«Сохранить операцию»**.

Если текст не распознался (например, плохое фото):

- нажмите **«Вставить текст вручную»** и вставьте текст чека — сумма, дата и категория
  будут извлечены из текста;
- либо просто заполните поля вручную — импорт необязателен.

> OCR работает офлайн, внутри контейнера. Внешним сервисам изображения не отправляются.
> Если в вашем образе нет `tesseract`, кнопка изображения вернёт понятную ошибку
> и предложит вставить текст — приложение продолжит работать.

---

## 5.2. Экспорт JSON для ИИ-анализа

**Настройки → Данные для ИИ-анализа → «Скачать JSON профиля»**.

Файл `schitayka-profile-<дата>.json` содержит:

| Раздел | Содержимое |
|---|---|
| `meta` | версия схемы, дата экспорта, приложение, валюта |
| `user` | имя, email, стартовый баланс, наличие семьи (без пароля и кода семьи) |
| `summary` | итоги месяца и за всё время, норма сбережений, общий баланс |
| `accounts` | счета: тип, баланс, лимит, кэшбэк, доступный остаток |
| `categories` | категории и число операций в каждой |
| `transactions` | все операции с датами, типами, счетами и комментариями |
| `limits` | лимиты по месяцам с фактом, остатком и процентом использования |
| `goals` | цели, прогресс, срок и требуемый взнос в месяц |
| `monthly` | доходы/расходы/баланс по месяцам |
| `expenseShare` | структура расходов: за текущий месяц и за всё время |
| `accountRules` / `alerts` / `reserves` / `reserveLoans` | правила счетов, уведомления, резервы и займы |

Экспортируются **только ваши данные**: нет пароля, JWT, кода семьи и чужих семейных счетов.
Файл содержит финансовую историю — передавайте его только доверенному ИИ-сервису.

---

## 6. Где лежат данные

SQLite-база — в именованном томе **`schitayka-data`**, внутри контейнера `/app/data`.

```bash
sudo docker volume inspect schitayka-data
```

### Резервная копия

```bash
sudo docker run --rm \
  -v schitayka-data:/data \
  -v /volume1/docker/schitayka:/backup \
  alpine tar czf /backup/schitayka-data-$(date +%F).tgz -C /data .
```

### Восстановление

Остановить приложение:

```bash
cd /volume1/docker/schitayka/deploy/xpenology
sudo docker compose down
```

Восстановить:

```bash
sudo docker run --rm \
  -v schitayka-data:/data \
  -v /volume1/docker/schitayka:/backup \
  alpine sh -c "rm -rf /data/* && tar xzf /backup/schitayka-data-2026-11-11.tgz -C /data"
```

Запустить:

```bash
sudo docker compose up -d
```

> Не удаляйте том `schitayka-data` при обновлениях. `docker compose down` том не удаляет,
> а `docker compose down -v` — **удаляет**.

---

## 7. Обновление проекта

### 7.1. Проверка версии в приложении

**Настройки → Версия и обновления**:

- текущая версия, коммит и дата сборки;
- последний коммит в GitHub, дата и сообщение;
- метка «Обновление» в шапке и точка у «Настроек», если доступна новая версия;
- кнопка «Проверить обновления»;
- команда обновления с кнопкой копирования.

Проверка идёт автоматически при входе и раз в 6 часов; на сервере результат кэшируется
на 10 минут. Без `GITHUB_TOKEN` GitHub разрешает 60 запросов/час — обычно достаточно.

Приложение не запускает `git`/`docker` само (у контейнера нет Docker-сокета).
Команду нужно выполнить на NAS.

### 7.2. Обновить код без пересоздания контейнера

```bash
cd /volume1/docker/schitayka
git pull --ff-only
sudo sh deploy/xpenology/update-inplace.sh --no-pull
```

Скрипт:

1. собирает новый образ;
2. останавливает контейнер;
3. копирует новый код в существующий контейнер;
4. запускает его снова.

ID/имя/порты/тома сохраняются, база не затрагивается. Простой — несколько секунд.

Если `git` нет или проект копировался без `.git`: замените файлы вручную и выполните
`sudo sh .../update-inplace.sh --no-pull`.

Если есть локальные изменения, `git pull --ff-only` остановится. Закоммитьте/спрячьте
изменения (`git stash`) или обновляйтесь вручную с `--no-pull`.

### 7.3. Полное обновление

Нужно, если менялись `docker-compose.yml`, `.env`, порты или базовый образ:

```bash
cd /volume1/docker/schitayka
git pull --ff-only
sudo sh deploy/xpenology/update.sh
```

`update.sh` делает `docker compose up -d --build`: контейнер пересоздаётся из нового
образа, но том `schitayka-data` сохраняется.

Ручной вариант без скрипта:

```bash
cd /volume1/docker/schitayka/deploy/xpenology
sudo docker compose up -d --build
```

### 7.4. Выпуск новой версии

Измените [`VERSION`](../../VERSION) (например, `1.2.0`), закоммитьте и запустите
обновление. При сборке версия попадёт в backend, коммит подставится скриптом.

---

## 8. HTTPS и доступ извне

Приложение слушает HTTP 8080 внутри контейнера; наружу — `APP_PORT` (5023).

Для HTTPS через Synology:

1. **Control Panel → Login Portal → Advanced → Reverse Proxy → Create**.
2. Источник: `https://finance.example.ru:443`.
3. Назначение: `http://127.0.0.1:5023`.
4. Сертификат — Let's Encrypt в **Control Panel → Security → Certificate**.

В compose уже включён `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`.

Не открывайте 5023 напрямую в интернет. Если доступ извне не нужен — не пробрасывайте
порт на роутере. Регистрация в приложении открыта; для публичного доступа ограничьте
её на уровне reverse proxy (Allow/Deny по IP) или VPN.

---

## 9. Сборка на ПК (если NAS слабый)

На компьютере с Docker Desktop:

```bash
docker build -f deploy/xpenology/Dockerfile -t schitayka:latest .
docker save schitayka:latest | gzip > schitayka-image.tgz
```

Загрузите файл на NAS:

```bash
gunzip -c schitayka-image.tgz | sudo docker load
cd /volume1/docker/schitayka/deploy/xpenology
sudo docker compose up -d --no-build
```

---

## 10. Обновление через Container Manager UI

Если не хотите SSH:

1. Замените файлы проекта на NAS (File Station / SMB).
2. **Container Manager → Проект → schitayka → Собрать/Запустить** (Build).
3. Дождитесь статуса Running/Healthy.

Это пересоздаст контейнер, но том `schitayka-data` сохранится. Для обновления «без
пересоздания» нужен SSH и `update-inplace.sh`.

---

## 11. Диагностика

| Проблема | Что сделать |
|---|---|
| `JWT_KEY is not set` | `cp .env.example .env`, заполнить `JWT_KEY`, перезапустить |
| `bind: address already in use` | сменить `APP_PORT` в `.env` |
| Контейнер `unhealthy` | `sudo docker logs schitayka`; `curl http://127.0.0.1:5023/api/health` |
| Страница не открывается | проверить `docker compose ps`, порт 5023, firewall DSM |
| `permission denied` на `/app/data` | не использовать bind-mount; вернуть именованный том |
| Container Manager не видит `../..` | запускать из `deploy/xpenology` или через SSH |
| `docker compose` не найдена | использовать `docker-compose` (v1) |
| Долго собирается | первый build скачивает SDK/Node; дальше кэш |
| Не хватает места | `sudo docker system df`, `sudo docker image prune -f`, `sudo docker builder prune -f` |
| Проверка обновлений: «Не удалось связаться с GitHub» | проверить интернет на NAS, DNS, SSL; опционально `GITHUB_TOKEN` |
| `update-inplace.sh` прервался | контейнер мог остаться остановленным: `sudo docker start schitayka`, повторить; том не пострадал |
| В логах `\r` / `not found` в скрипте | скрипт отредактирован в Windows; пересохраните с LF или `sed -i 's/\r$//' update*.sh` |
| 502 Bad Gateway за reverse proxy | проверьте destination `http://127.0.0.1:5023` и что контейнер healthy |

Полезные команды:

```bash
cd /volume1/docker/schitayka/deploy/xpenology
sudo docker compose ps
sudo docker compose logs --tail=100 schitayka
sudo docker compose restart schitayka
```

---

## 12. Удаление

```bash
cd /volume1/docker/schitayka/deploy/xpenology
sudo docker compose down          # контейнер удалён, том с базой остался
```

Полное удаление **с данными** (необратимо):

```bash
sudo docker compose down -v
```

> `-v` удаляет том `schitayka-data` вместе с базой. Без `-v` том остаётся.

---

## 13. Чек-лист

- [ ] Container Manager установлен, SSH включён;
- [ ] весь проект скопирован в `/volume1/docker/schitayka`;
- [ ] в `deploy/xpenology/.env` задан `JWT_KEY`, `SEED_DEMO=false`;
- [ ] `sudo docker compose up -d --build` завершился успешно;
- [ ] `docker compose ps` — Running/Healthy;
- [ ] `http://NAS_IP:5023` открывается, аккаунт создан;
- [ ] настроен backup тома `schitayka-data`;
- [ ] при необходимости настроен HTTPS reverse proxy;
- [ ] в **Настройки → Версия и обновления** видна версия и работает проверка GitHub;
- [ ] для обновлений используется `update-inplace.sh` (или `update.sh` при изменении compose/env).
