# Документация проекта «СчитайКа»

Точка входа для разработчика и ИИ-модели: что читать, где искать информацию и как поддерживать документацию в актуальном состоянии.

## 1. О проекте

**СчитайКа** — веб-приложение для личного и семейного учёта финансов.

- **Backend:** ASP.NET Core 8, EF Core, SQLite, JWT
- **Frontend:** React 18, TypeScript, Vite (SPA, hash-routing), сборка в `backend/wwwroot`
- **Деплой:** Docker / Container Manager (Synology, Xpenology)

| Параметр | Значение |
|---|---|
| Текущая версия | **0.3.2** (`VERSION`) |
| Последний релиз | https://github.com/NikolaYudin/schitayka-finance/releases/tag/v0.3.2 |
| Ветка релизной линии | `release-0.3.0` |
| Тесты | 77/77 (`dotnet test`) |
| Сборка | успешно (`dotnet build -c Release`, есть предупреждение `CS8602` в тестах) |
| CI | GitHub Actions: `dotnet build/test`, `tsc --noEmit`, `npm run build` |

## 2. Порядок чтения

1. **[README.md](README.md)** (этот файл) — навигация по документации.
2. **[STATUS.md](STATUS.md)** — текущее состояние, история версий, известные ограничения, договорённости.
3. **[CHECKLIST.md](CHECKLIST.md)** — рабочий чек-лист: выполнено по плану, новые задачи, бэклог.
4. **[../PLAN.md](../PLAN.md)** — актуальный план, реализованные этапы и оставшиеся задачи.
5. **[PROJECT-MAP.md](PROJECT-MAP.md)** — карта файлов, API, сервисов и потоков данных.
6. Инструкции по установке/сборке/релизу — см. раздел «Инструкции».

## 3. Инструкции

| Документ | Назначение |
|---|---|
| [`../README.md`](../README.md) | Возможности, локальный запуск, краткий API |
| [`../deploy/xpenology/INSTALL.md`](../deploy/xpenology/INSTALL.md) | Установка на DSM 7.2 / Xpenology (Container Manager) |
| [`../deploy/xpenology/README.md`](../deploy/xpenology/README.md) | Работа с git-клоном, обновление, бэкап, диагностика |
| [`../deploy/xpenology/release/PACKAGING.md`](../deploy/xpenology/release/PACKAGING.md) | Сборка и проверка релизных артефактов |
| [`../deploy/xpenology/release/README.md`](../deploy/xpenology/release/README.md) | Инструкция пользователя внутри `schitayka-<версия>-docker.zip` |
| [`../schitayka-design-prompt.md`](../schitayka-design-prompt.md) | Дизайн-бриф / визуальные ориентиры |

## 4. Правила поддержки документации

1. **Меняется поведение, API, схема БД или структура файлов** — обновить `STATUS.md`, при необходимости `PLAN.md`, `CHECKLIST.md`, `PROJECT-MAP.md` и `../README.md`.
2. **Выпущен релиз** — обновить версию/ссылки/историю в `STATUS.md`, `CHECKLIST.md`, `../PLAN.md` и этом файле.
3. **Новая задача** — добавлять в `CHECKLIST.md` (раздел «Новые задачи»), а при планировании — в `PLAN.md`.
4. **Рабочая база данных** (`data/app.db`, тома NAS) не изменяется и не пересоздаётся в рамках документационных/обычных задач.
5. Документация — на русском языке, в кодировке UTF-8.

## 5. Быстрые команды

```powershell
# Сборка (включая frontend -> backend/wwwroot)
dotnet build SchitayKa.sln -c Release

# Тесты
dotnet test SchitayKa.sln -c Release --no-build

# Локальный запуск backend (http://localhost:5023)
cd backend; dotnet run

# Frontend dev (Vite, прокси /api -> :5023)
cd frontend; npm run dev

# Сборка релизных артефактов (dist/)
pwsh -NoProfile -File deploy/xpenology/release/build-release.ps1
```

## 6. Куда смотреть по типу задачи

| Задача | Где искать |
|---|---|
| Состояние продукта, что сделано/не сделано | `STATUS.md`, `CHECKLIST.md`, `../PLAN.md` |
| Карта API/контроллеров/сервисов/файлов | `PROJECT-MAP.md` |
| UI (дашборд, страницы, роутинг) | `frontend/src/App.tsx`, `frontend/src/components.tsx`, `frontend/src/*Page.tsx` |
| Баланс/сводка/правила расчётов | `backend/Services/AccountBalanceService.cs`, `backend/Controllers/BudgetController.cs` |
| Авторизация | `backend/Controllers/AuthController.cs`, `frontend/src/LoginPage.tsx`, `frontend/src/auth.ts` |
| Банки и привязка к счетам | `backend/Controllers/BanksController.cs`, `frontend/src/App.tsx`, `frontend/src/AccountsPage.tsx` |
| Обновления приложения | `backend/Services/UpdateChecker.cs`, `backend/Services/SelfUpdateService.cs`, `backend/Controllers/VersionController.cs` |
| NAS/установка/релизные артефакты | `../deploy/xpenology/*`, `../deploy/xpenology/release/*` |
