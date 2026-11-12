# СчитайКа (SchitayKa) 🧮

Веб-приложение для учёта личных финансов: доходы и расходы, лимиты по категориям,
анализ бюджета, финансовые цели и персонализированные рекомендации.

## Возможности
- **Учёт операций** — быстрый ввод дохода/расхода (FAB ＋), журнал по месяцам с итогами.
- **Категории** — стартовый набор создаётся автоматически при регистрации; можно добавлять свои.
- **Лимиты бюджета** — месячные лимиты расходов по категориям с прогресс-барами и контролем перерасхода.
- **Финансовые цели** — копилки с целевой суммой и сроком; приложение считает, сколько нужно откладывать в месяц.
- **Анализ** — динамика доходов/расходов за 6 месяцев, структура расходов по категориям, норма сбережений.
- **Персональные рекомендации** — советы по данным пользователя: превышение лимитов, низкая норма сбережений,
  отсутствие подушки безопасности, крупные траты без лимита, нерегулярный учёт и др.
- **Версия и обновления** — приложение показывает текущую версию/коммит, проверяет последний коммит
  в GitHub и предлагает обновиться; на NAS есть скрипт замены кода без пересоздания контейнера.
- **Импорт из чека или скриншота** — кнопка «Скриншот или чек» в окне новой операции: OCR (tesseract)
  распознаёт фото чека или скриншот банка, приложение само подставляет сумму, дату, тип,
  продавца и категорию — остаётся только проверить и сохранить. Есть резервный разбор вставленного текста.
- **Экспорт JSON для ИИ** — в настройках одна кнопка: полный профиль (счета, операции, категории,
  лимиты, цели, правила, резервы, месячные агрегаты) одним файлом для анализа ИИ-агентом.
- **Оформление** — премиальный macOS-стиль: стекломорфизм, мягкие тени, крупные радиусы и системные цвета Apple
  (синий `#007AFF`, зелёный `#34C759`, красный `#FF3B30`, фиолетовый `#AF52DE`). Светлая, тёмная и системная тема;
  переключатель в шапке и в настройках, выбор запоминается.
- **Счета** — дебетовые/кредитные карты, наличные и вклады; карточки в сетке, архивные счета скрываются из итогов. Переводы между счетами не искажают доходы и расходы.
- **Вход** — email + пароль (PBKDF2, JWT). Яндекс-OAuth код присутствует, но отключён (`YandexOAuth:Enabled=false`). Запросы входа/регистрации ограничены rate limiting.
- **Демо-данные** — при первом запуске создаётся аккаунт `demo@schitayka.ru / demo123` с операциями, лимитами и целями. В production отключается переменной `SCHITAYKA_SEED_DEMO=false`.

## Архитектура
- `backend/` — ASP.NET Core 8 (Minimal hosting + контроллеры), SQLite через EF Core, JWT-автентификация, Swagger (`/swagger`).
- `frontend/` — React 18 + TypeScript + Vite (SPA с hash-роутингом), собирается в `backend/wwwroot`.
- Схема БД обновляется **EF Core Migrations** (`backend/Migrations`); старые базы без истории миграций автоматически помечаются baseline-миграцией без потери данных.
- `backend.Tests/` — xUnit-тесты, `.github/workflows/ci.yml` — сборка, тесты и проверка типов фронтенда.

### Структура проекта
```
SchitayKa.sln
backend/
  SchitayKa.Api.csproj      # автосборка фронтенда при F5 в Visual Studio
  Program.cs                # конфигурация, создание БД, демо-сид, раздача SPA
  appsettings.json          # Jwt, ConnectionStrings, YandexOAuth, FrontUrl
  Data/AppDbContext.cs       # User, Category, Transaction, SavingsGoal, BudgetLimit
  Controllers/
    AuthController.cs        # регистрация/вход, /me, Яндекс-OAuth (выключен)
    DataControllers.cs       # api/categories, api/transactions, api/goals, api/limits
    BudgetController.cs      # api/budget/summary (анализ + рекомендации), start-balance
    AccountsController.cs    # api/accounts + api/family (счета и семья)
    AccountRulesController.cs # api/account-rules, api/alerts, api/reserve-loans
frontend/
  src/
    index.css                # дизайн-система: токены, стекло, компоненты, адаптив
    icons.tsx                # минималистичные SVG-иконки в духе lucide
    App.tsx                  # каркас: sidebar, topbar, роутинг, настройки
    components.tsx           # дашборд, кошелёк, графики, быстрая операция, журнал
    AccountsPage.tsx         # счета и семья
    GoalsPage.tsx            # цели + лимиты месяца
    AccountRulesCard.tsx     # правила счетов
    LoginPage.tsx            # экран входа
    api.ts auth.ts theme.ts toasts.tsx main.tsx
  backend/Services/
    ReceiptOcrService.cs     # OCR через tesseract CLI (jpg/png/webp/bmp/tiff)
    ReceiptParser.cs         # эвристики: сумма, дата, тип, продавец, категория
    UpdateChecker.cs         # проверка последнего коммита в GitHub
    AppVersion.cs            # версия и коммит сборки
deploy/
  xpenology/                 # Dockerfile, docker-compose.yml и инструкция для Container Manager
VERSION                      # версия приложения (читается backend при сборке)
```

### Дизайн
Интерфейс построен по брифу `schitayka-design-prompt.md`:
- стеклянные поверхности (`backdrop-filter: blur(...) saturate(...)`), тонкие полупрозрачные границы и мягкие тени;
- крупные радиусы (до 32px), воздушные отступы и чёткая типографическая иерархия;
- дашборд: hero-блок общего баланса, кольцо нормы сбережений, KPI, карточки счетов в духе Apple Wallet,
  столбчатая динамика за 6 месяцев, кольцевая структура расходов, лимиты и рекомендации;
- плавные «яблочные» анимации появления, hover и раскрытия FAB; поддержка `prefers-reduced-motion`;
- адаптив: мобильное выдвижное меню, одноколоночные карточки и компактная шапка.

## Запуск (Windows, как в оригинальной наработке)
1. Установите [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) и [Node.js LTS](https://nodejs.org).
2. Один раз запустите `setup.cmd` (проверит окружение, `npm install`, `npm run build`).
3. Откройте `SchitayKa.sln` в Visual Studio и нажмите **F5** — фронтенд будет собираться автоматически,
   браузер откроется на <http://localhost:5023>.

## Запуск (кроссплатформенно)
```bash
cd frontend && npm install && npm run build   # сборка SPA в backend/wwwroot
cd ../backend && dotnet run                    # http://localhost:5023
```
Режим разработки без сборки SPA: `cd frontend && npm run dev` (Vite проксирует `/api` на :5023,
а бэкенд в Development сам проксирует страницы на Vite, если wwwroot отсутствует).

## Запуск в Docker / Synology (Container Manager)

### Вариант 1. Zip-дистрибутив (рекомендуется)

```powershell
pwsh -File deploy/xpenology/release/build-release.ps1
# → dist/schitayka-1.1.0-docker.zip
```

Архив распаковывается в `/volume1/docker` (получится `/volume1/docker/schitayka`),
и проект создаётся прямо в Container Manager — без git, SSH и правки путей.
Внутри уже есть `.env` с ключом, `scripts/` для бэкапа/обновления и подробный `README.md`.
Архитектура и инструкция: [`deploy/xpenology/release/PACKAGING.md`](deploy/xpenology/release/PACKAGING.md),
инструкция внутри архива — [`deploy/xpenology/release/README.md`](deploy/xpenology/release/README.md),
план — `PLAN.md`, Задача 4.

### Вариант 2. Из git-клона

Готовая папка для сборки: [`deploy/xpenology`](deploy/xpenology/README.md) — multi-stage `Dockerfile`,
`docker-compose.yml`, `.env.example` и пошаговая инструкция для DSM 7.2 / Xpenology.
**Полная инструкция с нуля:** [`deploy/xpenology/INSTALL.md`](deploy/xpenology/INSTALL.md).

```bash
cd deploy/xpenology
cp .env.example .env        # обязательно задать JWT_KEY (>= 32 символов)
docker compose up -d --build
```

Приложение: `http://<IP-НАС>:5023`. SQLite хранится в именованном томе `schitayka-data`,
демо-аккаунт в production отключён (`SEED_DEMO=false`), healthcheck — `/api/health`.

Обновление на NAS: `sh deploy/xpenology/update-inplace.sh` — заменяет код внутри
существующего контейнера и перезапускает его, не пересоздавая контейнер и не трогая
том с базой. Если менялись compose/env — `sh deploy/xpenology/update.sh`.
Подробнее: [`deploy/xpenology/README.md`](deploy/xpenology/README.md#7-версия-и-обновление).

> В Production ключ JWT обязателен: задайте `Jwt__Key` (не короче 32 байт) через переменную окружения,
> user-secrets или `appsettings.Production.json`. Без него приложение осознанно не стартует.

## Запуск в VS Code
1. Установите расширение **C#** (`ms-dotnettools.csharp`).
2. Откройте папку репозитория целиком (**File → Open Folder**), а не только `frontend`.
3. Нажмите **F5** → конфигурация «SchitayKa: backend (5023)»: VS Code выполнит `dotnet restore`,
   сборку и запустит сервер; браузер откроется на <http://localhost:5023>.
   Порт 5023 зафиксирован в `Program.cs`, поэтому он одинаков при любом способе запуска
   (VS, VS Code, `dotnet run`) и совпадает с прокси Vite.
4. Быстрый путь без отладчика: терминал → `cd backend && dotnet run`.
   Команду `dotnet run` внутри `frontend` выполнять не нужно — там нет .NET-проекта.
   Для фронтенда в dev-режиме используйте задачу **Tasks → Run Task → frontend-dev (vite :5173)**.

## API (кратко)
| Метод | Путь | Описание |
|---|---|---|
| POST | /api/auth/register · /login | получение JWT |
| GET | /api/auth/me | текущий пользователь |
| GET/POST/PUT/DELETE | /api/categories | справочник категорий |
| GET/POST/PUT/DELETE | /api/transactions?month=&year= | операции |
| GET/POST/PUT/DELETE | /api/goals, /api/goals/{id}/deposit | финансовые цели |
| GET/POST/DELETE | /api/limits?year=&month= | месячные лимиты (upsert) |
| GET | /api/budget/summary | сводка + рекомендации |
| POST | /api/budget/start-balance | стартовый баланс «на счетах» |
| GET | /api/version · /api/version/check | текущая версия и проверка обновлений по GitHub |
| POST | /api/import/receipt · /receipt/text | распознавание чека/скриншота и разбор текста |
| GET | /api/export/profile | полный JSON-профиль для ИИ-анализа |
| GET | /api/health | проверка работоспособности |

Все данные изолированы по пользователю (фильтрация по UserId из JWT).

## О проекте-источнике
Создано на основе наработки [NikolaYudin/Finance](https://github.com/NikolaYudin/Finance)
(«Семейный бюджет», MIT License) с переработкой под личный учёт финансов СчитайКа:
семейный контур заменён на персональные данные, добавлены анализ структуры расходов,
месячные лимиты и блок персональных рекомендаций.
