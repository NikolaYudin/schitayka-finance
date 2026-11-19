// Простой API-клиент с JWT из sessionStorage (единое хранилище — см. token.ts)
import { getToken, setToken, clearToken } from './token';
export { getToken, setToken, clearToken };

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) { super(message); this.status = status; }
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json', ...(options.headers as any || {}) };
  const token = getToken();
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(path, { ...options, headers });
  if (res.status === 401 && !path.includes('/auth/')) {
    clearToken();
    location.hash = '#/login';
  }
  if (!res.ok) {
    let msg = `Ошибка ${res.status}`;
    try {
      const j = await res.json();
      msg = j.error || JSON.stringify(j);
      if (typeof j.type === 'string' && j.title) msg = j.title; // ProblemDetails
    } catch { /* ignore */ }
    throw new ApiError(res.status, msg);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  get: <T>(p: string) => request<T>(p),
  post: <T>(p: string, body?: unknown) => request<T>(p, { method: 'POST', body: JSON.stringify(body ?? {}) }),
  put: <T>(p: string, body: unknown) => request<T>(p, { method: 'PUT', body: JSON.stringify(body) }),
  del: <T>(p: string) => request<T>(p, { method: 'DELETE' }),

  /** multipart/form-data: Content-Type выставляет браузер (boundary). */
  upload: async <T>(p: string, form: FormData): Promise<T> => {
    const headers: Record<string, string> = {};
    const token = getToken();
    if (token) headers['Authorization'] = `Bearer ${token}`;

    const res = await fetch(p, { method: 'POST', headers, body: form });
    if (res.status === 401) {
      clearToken();
      location.hash = '#/login';
    }
    if (!res.ok) {
      let msg = `Ошибка ${res.status}`;
      try {
        const j = await res.json();
        msg = j.error || j.title || msg;
      } catch { /* ignore */ }
      throw new ApiError(res.status, msg);
    }
    const text = await res.text();
    return (text ? JSON.parse(text) : undefined) as T;
  },

  /** Скачивание файла с JWT и корректным именем из Content-Disposition. */
  download: async (p: string, fallbackName: string): Promise<void> => {
    const headers: Record<string, string> = {};
    const token = getToken();
    if (token) headers['Authorization'] = `Bearer ${token}`;

    const res = await fetch(p, { headers });
    if (res.status === 401) {
      clearToken();
      location.hash = '#/login';
    }
    if (!res.ok) {
      let msg = `Ошибка ${res.status}`;
      try {
        const j = await res.json();
        msg = j.error || j.title || msg;
      } catch { /* ignore */ }
      throw new ApiError(res.status, msg);
    }

    const blob = await res.blob();
    const disposition = res.headers.get('Content-Disposition') || '';
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    const name = match ? decodeURIComponent(match[1]) : fallbackName;

    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1500);
  },
  getBanks: () => request<BankInfo[]>('/api/banks'),
};

// ---- Типы ----
export type TxType = 0 | 1; // 0 = доход, 1 = расход

export interface UserInfo { id: number; email: string; name: string; startingBalance?: number; familyCode?: string | null; hasPassword?: boolean; }
export interface Category {
  id: number; name: string; type: TxType; color: string;
  isSystem?: boolean; familyShared?: boolean;
}
export interface Tx {
  id: number; categoryId: number; type: TxType; amount: number; date: string;
  comment: string; categoryName?: string; categoryColor?: string;
  accountId?: number | null; accountName?: string | null;
  isTransfer?: boolean;
}
export type AccountType = 0 | 1 | 2 | 3 | 4; // дебет, кредит, наличные, вклад, прочее

/** Доходность счёта (Задача 5): ставка % годовых вместо кэшбэка для вкладов. */
export interface InterestInfo {
  interestRate: number;
  interestMode: number;              // 0 нет, 1 ежедневно+капитализация, 2 ежедневно+выплата, 3 в конце срока
  interestModeName: string;
  openedAt: string | null;
  maturesAt: string | null;
  interestTaxRate: number;
  interestDaily: number;
  interestMonthly: number;
  interestMonthlyNet: number;
  interestYearly: number;
  interestYearlyNet: number;
  interestNetTotal: number;
  interestNote: string;
  daysToMaturity: number | null;
}

export interface AccountInfo {
  id: number; name: string; type: AccountType; typeName: string; balance: number; startingBalance: number;
  creditLimit: number; cashbackRate: number; cashbackAccrued: number; color: string;
  isArchived: boolean; available: number; creditUsagePct: number | null;
  ownerId?: number; ownerName?: string;
  isFamily?: boolean;
  bankId?: number | null;
  bankName?: string | null;
  /** Брендовые данные банка (0.3/N6) для фирменного стиля карточки. */
  bankShortName?: string | null;
  bankColor?: string | null;
  interest?: InterestInfo | null;
}

/** Счёт члена семьи — виден в дашборде, но не входит в личный бюджет. */
export interface FamilyAccountRow {
  id: number; name: string; type: AccountType; typeName: string; balance: number;
  creditLimit: number; cashbackRate: number; cashbackAccrued: number; color: string;
  ownerId: number; ownerName: string | null;
  bankShortName?: string | null; bankColor?: string | null;
}

/** Мониторинг инвестиций (Задача 6). */
/** Банк справочника (Задача 6 + 0.3/N5): брендовые поля для карточек (N6). */
export interface BankInfo {
  id: number; name: string; code: string; country: string; isSystem: boolean;
  shortName?: string | null; brandColor?: string | null; logoUrl?: string | null;
  inUse?: boolean;
}
export interface HoldingSummary {
  id: number; name: string; ticker: string; type: number; quantity: number;
  avgPrice: number; currentPrice: number; invested: number; value: number; pnl: number; pnlPct: number;
  quoteSource?: number | null; quoteSourceName?: string;
  priceUpdatedAt?: string | null; quoteError?: string | null;
}
export interface HoldingInfo {
  id: number; name: string; ticker: string; assetType: number; quantity: number;
  avgPrice: number; currentPrice: number; currency: string; updatedAt: string;
  quoteSource?: number | null; quoteSourceName?: string;
  priceUpdatedAt?: string | null; quoteError?: string | null;
}
export interface PortfolioInfo {
  id: number; name: string; bankId: number; bankName: string;
  product: number; productName: string;
  strategy: number; strategyName: string; strategyHint: string; riskNote: string;
  currency: string; createdAt: string;
  invested: number; value: number; pnl: number; pnlPct: number;
  cashValue: number; investedValue: number; days: number;
  savingsAt6: number; savingsAt8: number; riskPremium: number; taxEstimate: number;
  quoteUpdatedAt?: string | null; quoteError?: string | null;
  holdings: HoldingSummary[];
}
export interface InvestmentsSummary {
  invested: number; value: number; pnl: number; pnlPct: number;
  cashValue: number; investedValue: number; days: number;
  savingsAt6: number; savingsAt8: number; riskPremium: number; taxEstimate: number;
  portfoliosCount: number; savingsRate: number; savingsRatePro: number; taxRate: number;
  quoteUpdatedAt: string | null; quoteError: string | null;
  quoteSourceName: string; autoRefreshMinutes: number;
}
/** Результат обновления котировок (пункт 1.1 плана). */
export interface QuoteRefreshResult {
  updated: number; failed: number; skipped: number; quoted: number;
  lastSuccessAt: string | null; lastError: string | null;
  source: string; autoRefreshMinutes: number;
}

/** Событие истории долга (Задача 2). */
export interface DebtEventInfo {
  id: number;
  kind: number;
  kindName: string;
  amount: number;
  date: string;
  note: string;
  transactionId: number | null;
  createdAt: string;
}

/** Долг: «я должен» или «мне должны» (Задача 2). */
export interface DebtInfo {
  id: number;
  direction: number;
  directionName: string;
  counterparty: string;
  name: string;
  principalAmount: number;
  remainingAmount: number;
  currency: string;
  issuedAt: string;
  dueDate: string | null;
  interestRate: number;
  terms: string | null;
  accountId: number | null;
  accountName: string | null;
  note: string;
  status: number;
  statusName: string;
  isOverdue: boolean;
  daysLeft: number | null;
  eventsCount: number;
  ownerId?: number;
  ownerName?: string | null;
  createdAt: string;
  events: DebtEventInfo[];
  /** Предстоящие платежи, связанные с долгом (Задача 4). */
  linkedObligations: Array<{
    id: number; name: string; amount: number; nextPaymentDate: string;
    statusName: string; isOverdue: boolean;
  }>;
}

/** Сводка по долгам для виджета дашборда. */
export interface DebtsSummary {
  iOwe: number;
  owedToMe: number;
  net: number;
  activeCount: number;
  closedCount: number;
  overdueCount: number;
  overdueAmount: number;
  nearest: {
    id: number; name: string; dueDate: string | null; remainingAmount: number;
  } | null;
}
/** Факт оплаты обязательного платежа (Задача 3). */
export interface ObligationPaymentInfo {
  id: number;
  amount: number;
  date: string;
  plannedDate: string;
  note: string;
  transactionId: number | null;
  createdAt: string;
}

/** Обязательный платёж (Задача 3). */
export interface ObligationInfo {
  id: number;
  name: string;
  payee: string;
  categoryId: number | null;
  categoryName: string | null;
  categoryColor: string | null;
  amount: number;
  periodicity: number;
  periodicityName: string;
  nextPaymentDate: string;
  paymentDay: number | null;
  intervalDays: number | null;
  accountId: number | null;
  accountName: string | null;
  remind: boolean;
  debtId: number | null;
  debtName: string | null;
  note: string;
  status: number;
  statusName: string;
  isOverdue: boolean;
  daysLeft: number;
  paymentsCount: number;
  ownerId?: number;
  ownerName?: string | null;
  lastPaymentDate: string | null;
  payments: ObligationPaymentInfo[];
}

/** Сводка по обязательным платежам для виджета дашборда. */
export interface ObligationsSummary {
  activeCount: number;
  pausedCount: number;
  completedCount: number;
  overdueCount: number;
  overdueAmount: number;
  dueIn7DaysCount: number;
  dueIn7DaysAmount: number;
  monthPlannedAmount: number;
  paidThisMonthAmount: number;
  next: {
    id: number; name: string; payee: string;
    nextPaymentDate: string; amount: number; isOverdue: boolean;
  } | null;
  upcoming: Array<{
    id: number; name: string; nextPaymentDate: string; amount: number; isOverdue: boolean;
  }>;
}

/** Строка календаря событий (Задача 5): событие из любого источника. */
export interface CalendarItem {
  key: string;
  id: number;
  source: number;
  sourceName: string;
  title: string;
  date: string;
  time: string | null;
  amount: number | null;
  isOverdue: boolean;
  debtId: number | null;
  obligationId: number | null;
  accountId: number | null;
  remind: boolean;
  note: string;
  typeName: string | null;
  ownerId?: number;
  ownerName?: string | null;
}

export interface InterestForecastRow {
  year: number; month: number; gross: number; tax: number; net: number;
  startBalance: number; endBalance: number;
}
export interface InterestForecast {
  accountId: number; accountName: string; rate: number; mode: number; modeName: string;
  balance: number; daily: number; monthlyAvg: number; yearly: number;
  grossTotal: number; taxTotal: number; netTotal: number; rows: InterestForecastRow[];
  note: string; openedAt: string | null; maturesAt: string | null; daysToMaturity: number | null; taxRate: number;
}
export interface FamilyMember { id: number; name: string; email: string; isOwner?: boolean; isCreator?: boolean; }
export interface FamilyBoardColumn {
  userId: number; name: string; email: string; isOwner: boolean; isMe: boolean;
  totalBalance: number; accounts: AccountInfo[];
}
export interface FamilyBoard { familyCode: string | null; columns: FamilyBoardColumn[]; }
export interface Goal {
  id: number; name: string; targetAmount: number; currentAmount: number;
  deadline: string | null; progress: number; monthsLeft: number | null; perMonth: number | null;
}
export interface LimitRow { id: number; categoryId: number; limit: number; name: string; color: string; spent: number; }
export interface ShareRow { categoryId: number; name: string; color: string; total: number; }
export interface MonthRow { year: number; month: number; income: number; expense: number; }
export interface Recommendation { level: 'danger' | 'warn' | 'info' | 'good'; icon: string; text: string; }
export type AccountRuleKind = 0 | 1 | 2; // 0 автоподстановка, 1 неснижаемый остаток, 2 резерв
export interface AccountRule {
  id: number; name: string; kind: AccountRuleKind; kindName: string;
  isEnabled: boolean; priority: number;
  accountId: number | null; accountName: string | null;
  txType: TxType | null; matchCategoryId: number | null; matchCategoryName: string | null;
  commentContains: string | null; amountMin: number | null; amountMax: number | null;
  setCategoryId: number | null; setCategoryName: string | null;
  threshold: number | null; defaultReturnDays: number | null; notifyInApp: boolean;
  createdAt: string;
}
export interface AccountAlert {
  id: number; accountId: number | null; ruleId: number | null; kind: string;
  level: 'warn' | 'danger'; message: string; balanceSnapshot: number; createdAt: string;
  accountName: string | null;
}
export interface ReserveLoan {
  id: number; accountId: number; accountName: string | null; ruleId: number | null;
  amount: number; takenAt: string; dueDate: string; returnedAt: string | null;
  transactionId: number | null; comment: string; isOverdue: boolean;
}
export interface ReserveInfo {
  ruleId: number; ruleName: string; accountId: number | null; accountName: string | null;
  threshold: number; balance: number; taken: number; free: number;
}

// ---- Версия и обновления ----
export interface CurrentVersion {
  version: string;
  commit: string;
  shortCommit: string;
  buildDate: string | null;
}
export interface LatestCommit {
  sha: string;
  shortSha: string;
  message: string;
  url: string;
  date: string | null;
  version: string | null;
}
export interface UpdateCheck {
  ok: boolean;
  error: string | null;
  current: CurrentVersion;
  latest: LatestCommit | null;
  updateAvailable: boolean;
  repoUrl: string;
  compareUrl: string | null;
  checkedAt: string;
  /** Коммит в репозитории новее сборки, но релиз (версия) тот же. */
  commitIsNewer?: boolean;
}

/** Перевод между своими и семейными счетами (Задача 7 + 0.3/N3). */
export interface Transfer {
  id: number;
  fromAccountId: number; fromAccountName: string;
  toAccountId: number; toAccountName: string;
  /** Владельцы счетов (N3): перевод виден всей семье — показываем, чьи счета. */
  fromOwnerId?: number; fromOwnerName?: string | null;
  toOwnerId?: number; toOwnerName?: string | null;
  /** N3, совместимость с вариантом «б»: инициатор/владелец записи перевода. */
  initiatorUserId?: number; initiatorName?: string;
  amount: number; date: string; comment: string; createdAt: string;
  fromTxId: number | null; toTxId: number | null;
}

/** Состояние автоматического обновления (Задача 9). */
export interface SelfUpdateStatus {
  enabled: boolean;
  canUpdate: boolean;
  status: string;              // Idle | Running | Downloading | Verifying | Extracting | BackingUp | Applying | Done | Failed | Restarting
  targetVersion: string | null;
  progress: number;
  error: string | null;
  installDir: string | null;
  currentVersion: string | null;
  startedAt: string | null;
  finishedAt: string | null;
  log: string[];
  running: boolean;
}

// ---- Импорт чека/скриншота ----
export interface ReceiptParseResult {
  amount: number | null;
  date: string;
  type: TxType;
  typeName: string;
  categoryId: number | null;
  categoryName: string | null;
  merchant: string | null;
  comment: string;
  confidence: number;
  warnings: string[];
  lines: string[];
  rawText: string;
}
export interface ScopeUser { id: number; name: string; isMe: boolean; }
export type DashboardScope = 'me' | 'family' | 'ids';

export interface Summary {
  year: number;
  month: number;
  /** Охват агрегатов (0.3/N4): me | family | ids. */
  scope?: DashboardScope;
  scopeIds?: number[];
  scopeUsers?: ScopeUser[] | null;
  /** N4, совместимость с обоими вариантами API. */
  scopeLabel?: string;
  scopeUserIds?: number[];
  startingBalance: number;
  balance: number;
  monthIncome: number;
  monthExpense: number;
  totalBalance: number;
  savingsRate: number;
  byMonth: MonthRow[];
  expenseShare: ShareRow[];
  limits: LimitRow[];
  goals: Goal[];
  accounts: AccountSummaryRow[];
  familyAccounts?: FamilyAccountRow[];
  familyTotalBalance?: number;
  cashbackTotal: number;
  recommendations: Recommendation[];
  alerts: AccountAlert[];
  reserveLoans: ReserveLoan[];
  reserves: ReserveInfo[];
  reserveTaken: number;
}

export interface AccountSummaryRow {
  id: number;
  name: string;
  type: AccountType;
  typeName: string;
  balance: number;
  creditLimit: number;
  cashbackRate: number;
  cashbackAccrued: number;
  color: string;
  bankShortName?: string | null;
  bankColor?: string | null;
}

export const fmt = (n: number) => new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 0 }).format(n) + ' ₽';

/**
 * Разбор даты из API. Бэкенд хранит время в UTC, но отдаёт ISO-строку
 * без указания зоны («2026-11-18T06:26:10»). Браузер в таком случае
 * считает её локальной — добавляем «Z», чтобы время отображалось
 * в часовом поясе пользователя.
 */
export function parseServerDate(value: string): Date {
  return new Date(/(?:Z|[+-]\d{2}:?\d{2})$/.test(value) ? value : `${value}Z`);
}
export const MONTHS_RU = ['Январь','Февраль','Март','Апрель','Май','Июнь','Июль','Август','Сентябрь','Октябрь','Ноябрь','Декабрь'];
