# СчитайКа — карта проекта

Что за что отвечает: файл → функционал. Документ нужен, чтобы не искать по всему
репозиторию. Сначала прочитайте [`README.md`](README.md) (точка входа) и
[`STATUS.md`](STATUS.md) (что сделано и какие есть дефекты).

Всего в репозитории ~121 файл под контролем git (без `node_modules`, `bin`, `obj`, `wwwroot`).

---

## 1. Дерево и роли каталогов

```
schitayka-finance/
├── backend/                  # ASP.NET Core 8: API, бизнес-логика, миграции, статика SPA
│   ├── Program.cs            # вся конфигурация и старт приложения (см. §2)
│   ├── Controllers/          # HTTP-концы: 12 файлов / 15 контроллеров (§3.1)
│   ├── Services/             # бизнес-логика и интеграции: 11 файлов (§3.2)
│   ├── Data/                 # EF-контекст (15 сущностей) и design-time фабрика (§3.3)
│   ├── Migrations/           # 9 миграций EF Core + snapshot (§3.4)
│   ├── wwwroot/              # артефакт сборки фронтенда (в git лежит последняя сборка)
│   ├── appsettings*.json     # JWT, строка подключения, SelfUpdate, OCR, Yandex
│   └── SchitayKa.Api.csproj  # проект + MSBuild-таргет автосборки фронтенда
├── backend.Tests/            # xUnit: 9 файлов, 56 тестов (§4)
├── frontend/                 # React 18 + TS + Vite; собирается в backend/wwwroot
│   └── src/                  # 15 модулей интерфейса (§5)
├── deploy/xpenology/         # Docker, compose, обновление, установка на NAS (§6)
│   └── release/              # упаковщик zip-дистрибутива и сборка app.zip для автообновления
├── docs/                     # эта документация (§7)
├── design-preview/           # скриншоты дизайна (dashboard/accounts/mobile, light/dark)
├── .github/workflows/ci.yml  # CI: dotnet build/test + tsc + npm build
├── .vscode/                  # launch.json (отладка :5023) и tasks.json (frontend-dev)
├── .tools/shot.mjs           # служебный скрипт: скриншоты через Chrome CDP
├── PLAN.md                   # план: этапы, задачи, бэклог B1–B21, журнал исправлений
├── README.md                 # возможности, локальный запуск, краткий API
├── VERSION                   # версия приложения (0.2.4), читается app.zip и упаковщиком
├── SchitayKa.sln             # решение: backend + backend.Tests
├── global.json               # SDK 8.0.404, rollForward latestMajor
├── dotnet-tools.json         # локальный инструмент dotnet-ef 8.0.11
├── setup.cmd                 # разовая подготовка: restore + npm install + npm run build
├── build-frontend.cmd        # совместимость: вызывает setup.cmd
├── schitayka-design-prompt.md# бриф дизайн-системы (стекло, цвета Apple, адаптив)
├── .dockerignore             # что не попадает в образ (bin, obj, node_modules, data)
├── .gitattributes            # LF/CRLF-политика
├── .gitignore                # app.db, dist/, node_modules, логи
└── LICENSE                   # MIT
```

---

## 2. `backend/Program.cs` — сердце конфигурации

Один файл отвечает за всё, что происходит до и во время старта:

| Блок | Строки | Что делает |
|---|---|---|
| Автосборка фронтенда | 16–187 | Если `SCHITAYKA_AUTOBUILD_FRONTEND=false`/`0` — пропускает; иначе `npm install` + `npm run build` в `frontend` при старте из VS. Ошибки сборки не валят приложение (бэклог B16) |
| JWT | ~200–225 | Ключ из `Jwt__Key`; в Development генерируется случайный, в Production при отсутствии ключа — осознанное падение |
| Путь к БД | 227–235 | `<ContentRoot>/data/app.db`, если строка подключения не задана или дефолтная. **Переопределяется `ConnectionStrings__Default`** |
| Legacy-гармонизация | 237–409 | Списки новых таблиц/колонок, baseline для очень старых баз, удаление копий БД без пользователей (признано долгом, бэклог B14) |
| Миграции | 411–470 | `PrepareDatabase()`: нет файла → `Migrate()`; нет `__EFMigrationsHistory` но есть `Users` → baseline `InitialCreate`; затем `Migrate()` |
| Демо-сид | 549–600 | Только Development или `SCHITAYKA_SEED_DEMO=true/1`; `demo@schitayka.ru / demo123`, `spouse@schitayka.ru`; стартовые категории супруге |
| Справочник банков | ~559 | `SeedBanks.Seed(db)` — системные банки (Т-Банк, Сбер, ВТБ, Альфа и др.) |
| Rate limiting | — | Политики `auth` (10/мин на IP) и `import` (20/мин на IP) |
| Статика и SPA | — | `wwwroot` + fallback на `index.html` (hash-роутинг), `/swagger`, `/api/health` |
| Порт | 5023 | Зафиксирован, совпадает с прокси Vite и `.vscode/launch.json` |

Файлы конфигурации: `backend/appsettings.json` (Jwt, ConnectionStrings, Update:RepoUrl,
SelfUpdate, Ocr, YandexOAuth, FrontUrl), `backend/appsettings.Development.json` (только логирование),
`backend/Properties/launchSettings.json` (профили запуска).

---

## 3. Backend

### 3.1 Контроллеры (`backend/Controllers/`)

| Файл | Базовый маршрут | Ответственность |
|---|---|---|
| `AuthController.cs` | `/api/auth` | `login` (PBKDF2 + JWT), `me`, `demo-status`, `yandex/start`, `yandex/callback` (выключен флагом); **публичная регистрация отключена** (0.3.2) |
| `DataControllers.cs` | `/api/categories`, `/api/transactions`, `/api/goals`, `/api/limits` | 4 контроллера в одном файле: справочник категорий, операции (фильтр по месяцу/году), цели (`POST {id}/deposit`), месячные лимиты (upsert) |
| `BudgetController.cs` | `/api/budget` | `GET summary` — сводка, анализ, рекомендации, счета семьи; охват агрегатов `scope=me|family|ids&ids=…` (0.3/N4: дашборд семьи, объединение одноимённых категорий, личные инструменты всегда свои); `POST start-balance`; `POST tx-for` — быстрая операция из сводки (пересчёт счетов в транзакции) |
| `AccountsController.cs` | `/api/accounts` | Счета: `GET`, `POST`, `PUT {id}`, `DELETE {id}`, `POST {id}/adjust`, `{id}/cashback`, `{id}/tx`; профиль `profile`; семья: `members`, `invite`, `invites`, `status`, `join`, `family-board`, `leave`; валидация `BankId`; статический `RecalcAccountsOf` |
| `AccountRulesController.cs` | `/api/account-rules` | Правила распределения по счетам: CRUD, `test`, `apply`, `{id}/dismiss`, `{id}/return` |
| `TransfersController.cs` | `/api/transfers` | Переводы между счетами: `GET`, `GET {id}`, `POST`, `DELETE {id}` — всё через `TransferService`; `Project` отдаёт владельцев счетов (`fromOwnerId/fromOwnerName/toOwnerId/toOwnerName`, 0.3/N3) |
| `InterestController.cs` | `/api/interest` | Вклады и накопительные счета: `forecast/{accountId}`, `accruals`, `accrue/{accountId}`, `payout/{accountId}` (сумма = `Net` неоплаченных, транзакция, защита от повторной выплаты) |
| `InvestmentsController.cs` | `/api/investments` | Портфели и позиции: CRUD, `summary`, `{id}/holdings` (GET/POST/PUT/DELETE) |
| `BanksController.cs` | `/api/banks` | Справочник банков (0.3/N5): системные + пользовательские; `GET ?search=` (возвращает `inUse`), `POST`, `PUT {id}`, `DELETE {id}` (только свои, если не используются), `DELETE /unused` (массовое удаление неиспользуемых своих); в UI — фильтр «Скрыть неиспользуемые»; идемпотентный `SeedBanks.Seed` |
| `ImportController.cs` | `/api/import` | `receipt` (фото/скриншот через OCR), `receipt/text` (разбор вставленного текста); проверка Content-Type **и** расширения, rate limit |
| `ExportController.cs` | `/api/export` | `GET profile` — полный JSON-профиль для ИИ-анализа |
| `VersionController.cs` | `/api/version` | `GET` — текущая версия/коммит; `check` — проверка обновлений; `POST apply` — запуск автообновления (только админ из `SelfUpdate:AdminEmails`); `apply/status`, `apply/log` |

### 3.2 Сервисы (`backend/Services/`)

| Файл | Ответственность |
|---|---|
| `AccountBalanceService.cs` | **Единственное место** расчёта остатка счёта: стартовый баланс + операции + проценты, без N+1. Используется контроллерами, `TransferService`, `AccountRulesController` |
| `AccountRulesService.cs` | Правила распределения: `MatchAssignmentAsync`, `EvaluateAllAsync`, алерты, резервные займы (`TakeReserveLoanAsync`, `ReturnReserveLoanAsync`) |
| `TransferService.cs` | Переводы между своими и семейными счетами (Задача 7 + 0.3/N3): запись `Transfer` принадлежит владельцу счёта-отправителя, расходная половина — ему, доходная — владельцу счёта-получателя; видимость, детали и отмена — всей семьёй (`CanManageTransferAsync` через `FamilyAccess`); `TransferTransactionIdsAsync` скрывает половины из журнала у всех членов семьи |
| `InterestCalculator.cs` | Проценты по вкладам: Act/365, капитализация, режимы выплаты, НДФЛ, прогноз за месяц/год |
| `InvestmentCalculator.cs` | Стоимость и доходность портфелей, налоговый ориентир, сравнение с накопительным счётом |
| `FamilyAccess.cs` | Проверки доступа к семейным данным: `ManageableUserIdsAsync`, `CanManageAccountAsync`, `IsFamilyAccountAsync` |
| `ReceiptOcrService.cs` | OCR через CLI `tesseract` (rus+eng), таймаут, поддерживаемые форматы |
| `ReceiptParser.cs` | Эвристики разбора текста чека: сумма, дата, тип, продавец, категория |
| `UpdateChecker.cs` | Проверка последнего коммита/релиза в GitHub (`Update:RepoUrl`), результат для `/api/version/check` |
| `SelfUpdateService.cs` | Автообновление: выбор версии, скачивание `schitayka-{version}-app.zip`, проверка SHA256 (fail-closed), распаковка, бэкап, замена файлов, перезапуск; семафор `_gate` против параллельных запусков. **Известен дефект прогресса — B21** |
| `AppVersion.cs` | Версия и коммит текущей сборки (используются `/api/version` и упаковщиком) |

### 3.3 Данные (`backend/Data/`)

`AppDbContext.cs` — EF-контекст и модель. 15 сущностей:

| Сущность | Назначение |
|---|---|
| `User` | Пользователь: email, хэш пароля, семья (`FamilyCode`), тема, признак супруга |
| `Category` | Категории доходов/расходов (стартовый набор + свои) |
| `Transaction` | Операция: сумма, тип (доход/расход), дата, категория, счёт, `IsTransfer` |
| `SavingsGoal` | Финансовая цель/копилка: сумма, срок, пополнения |
| `BudgetLimit` | Месячный лимит по категории, уникальный `(UserId, CategoryId, Year, Month)` |
| `Account` | Счёт: тип (карта, наличные, вклад), стартовый баланс, кэшбэк, ставка/режим процентов, `BankId`, архив, семейный доступ |
| `FamilyInvitation` | Приглашение в семью по коду |
| `AccountRule` | Правило распределения по счетам (условия, действия, статус) |
| `AccountAlert` | Алерт по счёту (сработавшее правило) |
| `ReserveLoan` | Резервный заём по правилу: взят/возвращён |
| `InterestAccrual` | Начисление процентов: период, `Gross`/`Tax`/`Net`, признак оплаты; индекс `(UserId, AccountId, From)` |
| `Transfer` | Перевод между счетами: счета, сумма, дата; связывает две операции |
| `Bank` | Банк: системный (`UserId = null`) или пользовательский |
| `InvestmentPortfolio` | Портфель: банк, продукт, стратегия |
| `InvestmentHolding` | Позиция портфеля: тикер/название, количество, цена, стоимость |

`AppDbContextFactory.cs` — design-time фабрика для `dotnet ef`; путь `backend/data/app.db`,
переопределяется переменной `SCHITAYKA_DB` (удобно для проверки на копии).

### 3.4 Миграции (`backend/Migrations/`)

| Миграция | Что меняет |
|---|---|
| `20261109065002_InitialCreate` | Базовая схема: пользователи, категории, операции, цели, лимиты |
| `20261109065409_StageAStabilization` | Этап A: счета, семья, архив, индексы |
| `20261109071436_AccountRules` | Правила счетов, алерты, резервные займы |
| `20261113073201_AddInterestAndInvestments` | Начисления процентов, портфели и позиции |
| `20261113085516_AddTransfers` | Таблица `Transfers` |
| `20261113114933_AddAccountBank` | Банки (`Banks`) и `Accounts.BankId` — **создана с пустыми `Up`/`Down`**, поэтому нужна следующая |
| `20261113125249_AddAccountBankColumn` | Реально добавляет `Accounts.BankId`, индекс и внешний ключ |
| `20261116095454_AddInterestAccrualIndex` | Индекс `IX_InterestAccruals_UserId_AccountId_From` (был только в комментарии) |
| `20261117071500_BanksDirectory` | Брендовые поля `Banks.ShortName/BrandColor/LogoUrl` (0.3/N5) |

`AppDbContextModelSnapshot.cs` — снимок модели, обновляется автоматически.

---

## 4. Тесты (`backend.Tests/`, xUnit, 77 тестов)

| Файл | Что проверяет |
|---|---|
| `StageATests.cs` | Этап A: счета, семья, архив, `IsTransfer` (в т.ч. игнорирование клиентского флага) |
| `Task0Tests.cs` | Задача 0: лимиты, цели, категории, базовая сводка |
| `TransferTests.cs` | Переводы: создание двух половин, отмена, влияние на баланс |
| `Task03N4N5Tests.cs` | Задачи 0.3: охват дашборда (`me`/`family`/`ids`, чужие id отбрасываются, объединение категорий по названию, личные рекомендации только при «только я»); банки: идемпотентный сид, дозаполнение брендовых полей, свой банк (создание/дубликат/цвет), защита системных, запрет удаления при привязанных счетах |
| `Task03N3FamilyTransferTests.cs` | Задача 0.3/N3: принадлежность перевода владельцу счёта-отправителя (половины по владельцам счетов), видимость семье, отмена членом семьи, недоступность чужому пользователю, скрытие половин из журнала |
| `InterestCalculatorTests.cs` | Формулы процентов, режимы выплаты, НДФЛ, прогнозы |
| `InvestmentCalculatorTests.cs` | Стоимость, доходность, сравнение с накопительным счётом |
| `ReceiptParserTests.cs` | Разбор сумм, дат, продавцов, типов операций |
| `SelfUpdateTests.cs` | Версия, asset-имя, проверка контрольной суммы, отказ при 404, та же версия не запускает обновление |
| `UpdateCheckerTests.cs` | Определение доступного обновления по релизам GitHub (N1) |
| `EndpointFixesTests.cs` | P0 аудита 16.11.2026: `tx-for` (200 + пересчёт), выплата процентов (сумма `Net`, защита от повтора) |

Тесты используют SQLite in-memory и вызывают контроллеры напрямую; HTTP-интеграционных
тестов нет (бэклог B18). Запуск: `dotnet test SchitayKa.sln --no-build`.

---

## 5. Frontend (`frontend/src/`)

| Файл | Ответственность |
|---|---|
| `main.tsx` | Точка входа React, монтирование `App` |
| `App.tsx` | Каркас SPA: sidebar/topbar, hash-роутинг (`#/accounts`, `#/journal`, `#/goals`, `#/budget`, `#/investments`), настройки (в т.ч. карточка «Банки», 0.3/N5: поиск, плитки с монограммой и брендовым цветом, свой банк; 0.3/N6: переключатель «Нейтральный/Фирменный» стиль карточек), блок обновления/версии, профиль, тема |
| `components.tsx` | Крупные блоки дашборда: `Dashboard` (в т.ч. переключатель охвата «Я / члены / вся семья», 0.3/N4, хранение в `localStorage('sk_scope')`), карточки `WalletCard`/`FamilyWalletCard` (0.3/N6: брендовый градиент + монограмма банка), `TxRow`, `QuickTxModal` (быстрая операция + OCR), `Journal` (журнал по месяцам; 0.3/N3: владельцы счетов в переводах) |
| `AccountsPage.tsx` | Вкладка «Счета»: карточки счетов (0.3/N6: брендовый аватар/монограмма по банку), редактирование, банк, вклад (ставка/режим), семейный доступ, `FamilyCard`, переводы (0.3/N3: владелец контрагента) |
| `GoalsPage.tsx` | Цели и месячные лимиты (вкладка задаётся `initialTab`: `goals` или `limits`), прогресс, пополнения |
| `InvestmentsPage.tsx` | Портфели, позиции, сводка и сравнение с накопительным счётом |
| `AccountRulesCard.tsx` | Правила счетов: список, тест, применение, отмена, резервные займы |
| `LoginPage.tsx` | Вход и регистрация |
| `api.ts` | Все запросы к API + типы DTO (в т.ч. `apply` — статус автообновления) |
| `auth.ts` | Хранение JWT и пользователя в `localStorage`, разбор токена |
| `theme.ts` | Светлая/тёмная/системная тема, запоминание выбора |
| `bankstyle.ts` | 0.3/N6: фирменный стиль карточек счёта по банку — `getBankCardStyle`/`setBankCardStyle` (`localStorage('sk_bankstyle')`, по умолчанию `neutral`), `bankOf`, `darken`, `bankMonogram` (монограмма из `ShortName`, без внешних логотипов) |
| `toasts.tsx` | Всплывающие уведомления (частичная замена `alert`) |
| `icons.tsx` | SVG-иконки в духе lucide |
| `plural.ts` | Склонение русских существительных после чисел |
| `index.css` | Дизайн-система: токены, стекломорфизм, компоненты, адаптив (~70 КБ, бэклог B17) |

Вне `src/`: `index.html`, `vite.config.ts` (прокси `/api` → `:5023`, сборка в `backend/wwwroot`),
`package.json`, `tsconfig.json`.

---

## 6. Deploy (`deploy/xpenology/`)

| Файл | Ответственность |
|---|---|
| `Dockerfile` | Multi-stage: сборка фронтенда и backend, финальный образ с `data`-томом |
| `docker-compose.yml` | Проект для Container Manager: порт 5023, том `schitayka-data`, `JWT_KEY`, `SEED_DEMO`, `SELFUPDATE_ENABLED`, `SELFUPDATE_ADMIN_EMAILS`, healthcheck `/api/health` |
| `.env.example` | Шаблон окружения (ключи и флаги) |
| `INSTALL.md` | Пошаговая установка с нуля на DSM 7.2 / Xpenology; п. 7.5 — автообновление |
| `README.md` | Сборка из git-клона, обновление, бэкап, диагностика |
| `update.sh` | Пересборка образа из `./app` и перезапуск (том с базой не трогается) |
| `update-inplace.sh` | `git pull` + сборка `schitayka:latest` и замена файлов внутри контейнера без пересоздания |
| `release/build-release.ps1` | Упаковщик: `docker.zip` (~0,23 МБ, 88 файлов) + `app.zip` (артефакт автообновления) + `.sha256`. Ключ `-NoEnv` — для публичных релизов |
| `release/build-release.sh` | То же для Linux/macOS/CI |
| `release/PACKAGING.md` | Что попадает в архив, автопроверки, ручной чек-лист, порядок выпуска |
| `release/README.md` | Инструкция, которая кладётся внутрь архива |
| `release/docker-compose.yml`, `release/docker-compose.image.yml` | Compose для архива (сборка из `./app` / готовый образ) |
| `release/scripts/init-env.sh` | Создаёт `.env` из шаблона и генерирует `JWT_KEY` |
| `release/scripts/update.sh` | Обновление существующего проекта из `./app` |
| `release/scripts/backup.sh` / `restore.sh` | Согласованный бэкап SQLite из тома и восстановление |

Релизные артефакты и заметки складываются в `dist/` (в `.gitignore`).

---

## 7. Документация (`docs/`)

| Файл | Ответственность |
|---|---|
| `README.md` | Точка входа: порядок чтения, правила работы, ссылки на все инструкции, быстрые команды |
| `STATUS.md` | История версий, результаты аудита, известные дефекты (B21 и др.), что осталось |
| `PROJECT-MAP.md` | Этот файл: структура и ответственность файлов |

Внешние документы: `README.md` (корень), `PLAN.md`, `deploy/xpenology/INSTALL.md`,
`deploy/xpenology/release/PACKAGING.md`, `schitayka-design-prompt.md`.

---

## 8. Потоки данных

**Обычный запрос**
```
браузер → api.ts → JWT в Authorization → контроллер → сервис → EF Core → SQLite (app.db)
```
Пользователь определяется только из JWT; все выборки фильтруются по `UserId`
(семейный доступ — через `FamilyAccess`).

**Перевод между счетами**
```
POST /api/transfers → TransferService.Create
  → запись Transfer + две Transaction (IsTransfer=true, сервер ставит флаг)
  → AccountBalanceService пересчитывает остатки
  → DELETE /api/transfers/{id} удаляет обе половины и пересчитывает
```

**Проценты по вкладу**
```
GET  /api/interest/forecast/{accountId} → InterestCalculator (Act/365, НДФЛ, прогноз)
POST /api/interest/accrue/{accountId}   → InterestAccrual (Gross/Tax/Net, Paid=false)
POST /api/interest/payout/{accountId}   → одна операция на сумму Net неоплаченных, всё в транзакции
```

**Автообновление**
```
POST /api/version/apply (только админ из SelfUpdate:AdminEmails)
  → SelfUpdateService: версия → скачивание app.zip → SHA256 (.sha256 рядом)
  → распаковка → бэкап текущих файлов → замена файлов (data/ пропускается) → Environment.Exit
  → контейнер поднимается заново (restart: unless-stopped)
Статус: GET /api/version/apply/status, лог: GET /api/version/apply/log
```

**Старт приложения**
```
Program.cs → путь к БД → PrepareDatabase() → Migrate() → SeedBanks → (демо-сид по флагу)
  → маршруты API → статика wwwroot + SPA-fallback → http://0.0.0.0:5023
```

---

## 9. Где искать функционал

| Хочу поправить | Файл |
|---|---|
| Остаток на счёте, «сколько всего денег» | `backend/Services/AccountBalanceService.cs` |
| Сводка бюджета, анализ, рекомендации | `backend/Controllers/BudgetController.cs` |
| Операции, категории, цели, лимиты | `backend/Controllers/DataControllers.cs` |
| Счета, семья, банк счёта | `backend/Controllers/AccountsController.cs`, `Services/FamilyAccess.cs` |
| Переводы | `backend/Services/TransferService.cs`, `Controllers/TransfersController.cs` |
| Проценты вкладов | `backend/Services/InterestCalculator.cs`, `Controllers/InterestController.cs` |
| Инвестиции | `backend/Services/InvestmentCalculator.cs`, `Controllers/InvestmentsController.cs` |
| Импорт чека / OCR | `backend/Controllers/ImportController.cs`, `Services/ReceiptOcrService.cs`, `Services/ReceiptParser.cs` |
| Экспорт профиля | `backend/Controllers/ExportController.cs` |
| Проверка и установка обновлений | `backend/Services/SelfUpdateService.cs`, `Controllers/VersionController.cs` |
| Схема БД | `backend/Data/AppDbContext.cs` + новая миграция в `backend/Migrations/` |
| Демо-данные и первичный сид | `backend/Program.cs` (блок `SeedDemoDataAsync`) |
| Дашборд и его карточки | `frontend/src/components.tsx` |
| Роутинг, меню, настройки, тема | `frontend/src/App.tsx` |
| Запросы к API и типы | `frontend/src/api.ts` |
| Стили и дизайн-токены | `frontend/src/index.css`, бриф `schitayka-design-prompt.md` |
| Docker-образ и запуск на NAS | `deploy/xpenology/Dockerfile`, `docker-compose.yml`, `INSTALL.md` |
| Сборка релиза | `deploy/xpenology/release/build-release.ps1`, `PACKAGING.md` |

---

## 10. Соглашения по коду

- Комментарии и документация — по-русски; имена переменных/функций — по-английски.
- Контроллеры тонкие: бизнес-логика — в `Services/`, расчёт остатка — только в `AccountBalanceService`.
- Все операции, меняющие деньги/баланс, выполняются в транзакции и затем пересчитывают счета.
- Новые поля модели — только через миграцию; `Program.cs`-«гармонизация» не расширяется (B14).
- Ошибки пользователю — 400 с понятным текстом; 500 — только на непредвиденных сбоях.
- Фронтенд: типы DTO в `api.ts`, никаких прямых `fetch` из компонентов, уведомления — через `toasts.tsx`.
