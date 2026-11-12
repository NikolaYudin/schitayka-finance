# СчитайКа — установка на Synology / Xpenology

Готовый дистрибутив для **Container Manager**: распаковал в папку `docker` — создал проект — работает.
Собирать ничего не нужно: образ .NET + фронтенд собирается автоматически при первом запуске проекта.

**Версия:** см. файл `VERSION`.

---

## 1. Что внутри

```
schitayka/
├── docker-compose.yml     # проект для Container Manager (сборка из ./app)
├── docker-compose.image.yml  # вариант без сборки — готовый образ (когда опубликован)
├── .env                   # настройки + сгенерированный JWT_KEY (секрет!)
├── .env.example           # шаблон, если .env потерян
├── VERSION                # версия дистрибутива
├── README.md              # этот файл
├── MANIFEST.txt           # что и когда упаковано
├── app/                   # исходники и Dockerfile (контекст сборки)
│   ├── Dockerfile
│   ├── backend/
│   ├── frontend/
│   └── VERSION
└── scripts/
    ├── init-env.sh        # создать .env и сгенерировать ключ
    ├── update.sh          # пересобрать и перезапустить
    ├── backup.sh          # резервная копия базы
    └── restore.sh         # восстановление базы
```

Данные (SQLite) лежат **не в архиве**, а в именованном томе Docker `schitayka-data`.
Пересборка и обновление архива данные не затрагивают.

---

## 2. Требования

| Что | Минимум | Рекомендуется |
|---|---|---|
| DSM / Xpenology | 7.2 (Container Manager) | 7.2.1+ |
| RAM | 2 ГБ | 4 ГБ и больше |
| Свободное место | 3 ГБ (образы .NET SDK + Node) | 5 ГБ |
| Процессор | x86_64 | x86_64 |

> На ARM-моделях (например, DS223j — Realtek ARM) сборка .NET-образа может не работать.
> Для них используйте вариант с готовым образом — раздел 9.

---

## 3. Установка: три шага

### Шаг 1. Распаковать архив в папку `docker`

1. **File Station** → общая папка `docker` (обычно `/volume1/docker`).
2. Загрузите сюда zip-архив.
3. Правой кнопкой по архиву → **Извлечь** → **Извлечь сюда**.
4. Должно получиться:

```
/volume1/docker/schitayka/docker-compose.yml
/volume1/docker/schitayka/.env
/volume1/docker/schitayka/app/...
```

> Если архив распаковался с вложенной папкой (например, `schitayka-1.1.0/`), переименуйте её
> в `schitayka` — имя папки станет именем проекта в Container Manager.

### Шаг 2. Проверить `.env`

В архиве уже лежит `.env` со **сгенерированным** `JWT_KEY` — менять ничего не обязательно.
Проверить/поправить можно в File Station (правой кнопкой по `.env` → **Изменить**):

```ini
APP_PORT=5023                      # порт приложения на NAS
JWT_KEY=<48 случайных байт base64> # секрет; не публикуйте
TZ=Europe/Moscow
SEED_DEMO=false                    # true — создать demo-аккаунт для теста
```

Если `.env` нет (архив собран без секретов) — создайте его по SSH:

```bash
cd /volume1/docker/schitayka
sudo sh scripts/init-env.sh
```

### Шаг 3. Создать проект в Container Manager

1. **Container Manager** → **Проект** → **Создать**.
2. **Имя проекта:** `schitayka`.
3. **Путь:** `/docker/schitayka` (папка, куда распаковали архив).
4. **Источник:** «Использовать существующий `docker-compose.yml`» → выберите файл.
5. **Далее → Готово.** Начнётся сборка образа: на слабом NAS это 10–25 минут,
   на быстром — 5–10. Прогресс виден в разделе **Проект** и в **Журнале**.
6. Дождитесь статуса **Работает / Healthy**.

> Если мастер не принимает путь или не показывает сборку — используйте SSH (раздел 8):
> `sudo sh scripts/update.sh`.

---

## 4. Проверка

```bash
curl http://127.0.0.1:5023/api/health
```

Ожидаемый ответ:

```json
{"status":"ok","app":"СчитайКа"}
```

В браузере: `http://<IP-НАС>:5023`.

---

## 5. Первый вход

- `SEED_DEMO=false` (по умолчанию) — откройте вкладку **«Регистрация»** и создайте аккаунт.
  Стартовые категории создадутся автоматически.
- `SEED_DEMO=true` — можно войти как `demo@schitayka.ru` / `demo123` (только для теста).

---

## 6. Данные и резервные копии

- База: том Docker **`schitayka-data`** → `/app/data/app.db` внутри контейнера.
- Том переживает пересборку, обновление и перезапуск.
- **Удаление проекта в Container Manager с галочкой «удалить данные» уничтожит базу.**

### Резервная копия

```bash
cd /volume1/docker/schitayka
sudo sh scripts/backup.sh
```

Файл появится в `backups/schitayka-data-<дата>.tar.gz`. Контейнер остановится на несколько секунд —
это нужно, чтобы копия SQLite была целостной.

Копию можно скачать через File Station и хранить в Hyper Backup.

### Восстановление

```bash
cd /volume1/docker/schitayka
sudo sh scripts/restore.sh backups/schitayka-data-20261112-153000.tar.gz
```

Скрипт спросит подтверждение и заменит данные в томе.

---

## 7. Обновление

Обновление не трогает `.env` и том с базой.

1. Скачайте новый zip.
2. **Container Manager** → проект `schitayka` → **Остановить**.
3. Распакуйте новый архив в отдельную папку, например `/volume1/docker/schitayka-new`.
4. В File Station перенесите из неё в `/volume1/docker/schitayka`:
   - папку `app` (с заменой),
   - `docker-compose.yml`,
   - папку `scripts`.
   **Файл `.env` не заменяйте** — в нём ваш ключ и настройки.
5. В Container Manager → проект → **Сборка** (Build) → **Запуск**.
   Либо по SSH: `sudo sh scripts/update.sh`.

Проверка версии — в приложении: **Настройки → О приложении** (и плашка «Обновление» в шапке,
если в GitHub появился новый коммит).

---

## 8. Управление по SSH (если UI не подходит)

```bash
sudo -i
cd /volume1/docker/schitayka

cp -n .env.example .env      # только если .env отсутствует
vi .env                      # JWT_KEY

docker compose up -d --build # первая сборка и запуск
docker compose ps
docker logs -f schitayka
```

В старых DSM команда может быть `docker-compose` (v1) вместо `docker compose` (v2).

---

## 9. Слабый NAS: готовый образ без сборки

Если сборка на NAS невозможна (мало RAM, ARM), используйте образ из GHCR — когда он опубликован:

```bash
cd /volume1/docker/schitayka
cp docker-compose.yml docker-compose.build.yml
cp docker-compose.image.yml docker-compose.yml
sudo sh scripts/update.sh --no-build
```

Или в Container Manager замените содержимое `docker-compose.yml` на вариант из
`docker-compose.image.yml` и нажмите **Сборка → Запуск**.

---

## 10. HTTPS и доступ извне

1. **Панель управления → Вход → Дополнительно → Обратный прокси** (DSM 7.2: «Вход и безопасность»).
2. Источник: `https://finance.example.ru:443`, назначение: `http://127.0.0.1:5023`.
3. Сертификат — Let's Encrypt через DSM.
4. В `.env` ничего менять не нужно: `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` уже включён.

---

## 11. Диагностика

| Симптом | Причина / решение |
|---|---|
| Проект не создаётся, «неверный путь» | Убедитесь, что `docker-compose.yml` лежит **в корне** `/volume1/docker/schitayka` |
| `JWT_KEY is not set` | Нет `.env` или пустой ключ → `sudo sh scripts/init-env.sh` |
| Порт занят | Смените `APP_PORT` в `.env` и перезапустите проект |
| Контейнер перезапускается | `docker logs --tail 100 schitayka` — обычно ключ JWT или права на том |
| 502 от обратного прокси | Проверьте `curl http://127.0.0.1:5023/api/health` на NAS |
| Импорт чека: «OCR не настроен» | В образе нет `tesseract` (нестандартная сборка) — вставьте текст чека вручную |
| Сборка падает по памяти | Соберите образ на ПК (`docker save`/`load`) или используйте готовый образ (раздел 9) |

Логи:

```bash
docker logs --tail 200 schitayka
docker compose -f /volume1/docker/schitayka/docker-compose.yml ps
```

---

## 12. Удаление

1. **Container Manager → Проект → Остановить**, затем **Удалить**.
2. Галочку «Удалить данные/том» ставьте **только** если база больше не нужна.
3. Удалите папку `/volume1/docker/schitayka`.

Том удаляется отдельно:

```bash
docker volume rm schitayka-data
```

---

## 13. Чек-лист

- [ ] Архив распакован в `/volume1/docker/schitayka` (compose в корне)
- [ ] `.env` существует, `JWT_KEY` длиннее 32 символов
- [ ] Проект `schitayka` создан в Container Manager, статус **Работает**
- [ ] `curl http://127.0.0.1:5023/api/health` отвечает `{"status":"ok"}`
- [ ] Аккаунт создан через UI
- [ ] Сделана первая резервная копия (`scripts/backup.sh`)
