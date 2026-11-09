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
- **Оформление** — светлая, тёмная и системная тема: переключатель в шапке и в настройках, выбор запоминается.
- **Счета** — дебетовые/кредитные карты, наличные и вклады; карточки в сетке, архивные счета скрываются из итогов. Переводы между счетами не искажают доходы и расходы.
- **Вход** — email + пароль (PBKDF2, JWT). Яндекс-OAuth код присутствует, но отключён (`YandexOAuth:Enabled=false`). Запросы входа/регистрации ограничены rate limiting.
- **Демо-данные** — при первом запуске создаётся аккаунт `demo@schitayka.ru / demo123` с операциями, лимитами и целями.

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
frontend/
  src/
    api.ts auth.ts main.tsx App.tsx LoginPage.tsx components.tsx GoalsPage.tsx index.css
```

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

> В Production ключ JWT обязателен: задайте `Jwt__Key` (не короче 32 байт) через переменную окружения,
> user-secrets или `appsettings.Production.json`. Без него приложение осознанно не стартует.

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
| GET | /api/health | проверка работоспособности |

Все данные изолированы по пользователю (фильтрация по UserId из JWT).

## О проекте-источнике
Создано на основе наработки [NikolaYudin/Finance](https://github.com/NikolaYudin/Finance)
(«Семейный бюджет», MIT License) с переработкой под личный учёт финансов СчитайКа:
семейный контур заменён на персональные данные, добавлены анализ структуры расходов,
месячные лимиты и блок персональных рекомендаций.
