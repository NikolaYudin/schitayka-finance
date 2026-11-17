# СчитайКа — документация проекта

Точка входа для разработчика и для ИИ-модели, которая подключается к проекту
без истории переписки. Здесь — что читать, в каком порядке и где что лежит.

---

## 1. Что это за проект

**СчитайКа** — веб-приложение для личного и семейного учёта финансов.
Backend: ASP.NET Core 8 (контроллеры) + SQLite через EF Core + JWT.
Frontend: React 18 + TypeScript + Vite (SPA с hash-роутингом), собирается в `backend/wwwroot`.
Разворачивается на Synology / Xpenology в Docker (Container Manager).

| Параметр | Значение |
|---|---|
| Текущая версия | **0.3.2** (файл `VERSION`) |
| Последний релиз | https://github.com/NikolaYudin/schitayka-finance/releases/tag/v0.3.2 |
| Тесты | 77 (xUnit), все зелёные |
| Сборка | 0 ошибок (есть 1 предупреждение в тестах) |
| CI | GitHub Actions: `dotnet build/test` + `tsc --noEmit` + `npm run build` |
| Ветка релизов | `release-0.3.0` |

---

## 2. Порядок чтения (для модели — начните здесь)

1. **Этот файл** — общая картина и ссылки.
2. **[`STATUS.md`](STATUS.md)** — что сделано, что не сделано, известные дефекты и договорённости.
   Читать обязательно: там зафиксированы решения, которые уже приняты, и то, что трогать не нужно.
3. **[`PROJECT-MAP.md`](PROJECT-MAP.md)** — карта: за какой файл что отвечает, где искать функционал.
4. **[`CHECKLIST.md`](CHECKLIST.md)** — рабочий чек-лист задач (что сделано / что добавить / бэклог).
5. **[`../PLAN.md`](../PLAN.md)** — полный план работ, критерии готовности, бэклог, журнал исправлений.
6. Дальше — по задаче: инструкции по установке и упаковке (раздел 4).

Не нужно перечитывать весь репозиторий: `STATUS.md` + `PROJECT-MAP.md` дают достаточный контекст,
а `PLAN.md` — историю решений.

Новые задачи (0.3): см. раздел «Задачи 0.3» в [`../PLAN.md`](../PLAN.md) и
[`STATUS.md`, §8](STATUS.md#8-предложения-пользователя-16112026-задачи-03) — обновление на ту же
версию, макеты по Т-Банку, переводы между счетами семьи, охват дашборда, банки РФ, стилистика карточки.

---

## 3. Правила работы с проектом

1. **Данные пользователя неприкосновенны.** Рабочая база — `data/app.db` (в `.gitignore`).
   Не изменять, не пересоздавать, не запускать `EnsureDeleted()` на ней.
   Проверять миграции — только на копии в temp, после проверки копию удалять.
2. **Схема БД меняется только миграциями EF Core** (`dotnet ef migrations add <Name> --project backend`).
   Старые базы получают baseline-миграцию без потери данных.
3. **Баланс считается в одном месте** — `backend/Services/AccountBalanceService.cs`.
   Новые формулы остатка не дублировать.
4. **Признак `IsTransfer` ставит только сервер** (`TransferService`), значение из клиента игнорируется.
5. **Демо-данные** создаются только в Development или при `SCHITAYKA_SEED_DEMO=true`.
6. **Версия** меняется в `VERSION` + отдельный коммит `chore(release): version x.y.z`;
   пользовательские изменения — отдельным осмысленным коммитом до него.
7. **Секреты в архив не кладутся**: релизный `docker.zip` собирается с `-NoEnv`,
   ключ `JWT_KEY` создаёт `scripts/init-env.sh` на NAS.
8. Комментарии и документация — на русском, идентификаторы в коде — на английском.

---

## 4. Где какая инструкция

| Документ | Для кого | О чём |
|---|---|---|
| [`STATUS.md`](STATUS.md) | разработчик, ИИ | история версий, текущее состояние, известные дефекты, что дальше |
| [`CHECKLIST.md`](CHECKLIST.md) | разработчик, ИИ, пользователь | чек-лист задач: выполнено по плану, новые задачи, бэклог |
| [`PROJECT-MAP.md`](PROJECT-MAP.md) | разработчик, ИИ | структура репозитория, ответственность файлов, потоки данных |
| [`../PLAN.md`](../PLAN.md) | разработчик, ИИ | этапы A и 0.1–0.3, задачи 1–9, бэклог, критерии готовности |
| [`../README.md`](../README.md) | пользователь, разработчик | возможности, локальный запуск (VS / VS Code / CLI), краткий API |
| [`../deploy/xpenology/INSTALL.md`](../deploy/xpenology/INSTALL.md) | администратор NAS | установка с нуля на DSM 7.2 / Xpenology, п. 7.5 — автообновление |
| [`../deploy/xpenology/README.md`](../deploy/xpenology/README.md) | администратор NAS | сборка из git-клона, обновление, бэкап, диагностика |
| [`../deploy/xpenology/release/PACKAGING.md`](../deploy/xpenology/release/PACKAGING.md) | разработчик | как собрать и проверить zip-дистрибутив, порядок выпуска |
| [`../deploy/xpenology/release/README.md`](../deploy/xpenology/release/README.md) | пользователь архива | инструкция, которая лежит внутри `schitayka-<версия>-docker.zip` |
| [`../schitayka-design-prompt.md`](../schitayka-design-prompt.md) | дизайнер, разработчик | бриф дизайн-системы (стекло, типографика, цвета Apple) |
| [`STATUS.md`, раздел 3](STATUS.md#3-история-версий) | все | хронология релизов 0.1.0 → 0.2.4 |

---

## 5. Быстрые команды

```powershell
# сборка (заодно собирает фронтенд в backend/wwwroot)
dotnet build SchitayKa.sln

# тесты
dotnet test SchitayKa.sln --no-build

# локальный запуск (http://localhost:5023)
cd backend; dotnet run

# только фронтенд в dev-режиме (Vite проксирует /api на :5023)
cd frontend; npm run dev

# новая миграция
dotnet ef migrations add <Name> --project backend

# релиз: артефакты в dist/ (docker.zip + app.zip + .sha256)
pwsh -File deploy/xpenology/release/build-release.ps1 -NoEnv
```

Проверка миграций на копии рабочей базы (безопасно):

```powershell
$tmp = "$env:TEMP\sk-check"; New-Item -ItemType Directory -Force $tmp | Out-Null
Copy-Item data/app.db "$tmp\app.db"
$env:ASPNETCORE_ENVIRONMENT='Development'; $env:ASPNETCORE_URLS='http://127.0.0.1:5601'
$env:SCHITAYKA_SEED_DEMO='false'; $env:SCHITAYKA_AUTOBUILD_FRONTEND='false'
$env:ConnectionStrings__Default="Data Source=$tmp\app.db"
dotnet backend/bin/Debug/net8.0/SchitayKa.Api.dll   # смотреть лог, затем остановить
```

> Запускать именно из отдельного каталога (`workdir = $tmp`), иначе `Program.cs`
> найдёт `data/app.db` рядом с контентом и «гармонизирует» его.

---

## 6. Что делать в первую очередь, если пришла задача

| Задача | Куда смотреть |
|---|---|
| «не работает эндпоинт / 500» | `PROJECT-MAP.md` → таблица маршрутов → контроллер; затем `PLAN.md` бэклог B18 |
| «поправить расчёт баланса/сводки» | `backend/Services/AccountBalanceService.cs`, `backend/Controllers/BudgetController.cs` |
| «поправить UI» | `PROJECT-MAP.md` §5: `App.tsx` (каркас и роутинг), `components.tsx` (дашборд), страницы-вкладки |
| «обновление/автообновление» | `backend/Services/SelfUpdateService.cs`, `VersionController.cs`, `PLAN.md` B21 |
| «установка на NAS» | `deploy/xpenology/INSTALL.md` |
| «выпустить релиз» | `PROJECT-MAP.md` §6 и §9, `deploy/xpenology/release/PACKAGING.md` |

История и договорённости — в [`STATUS.md`](STATUS.md).
