// Простой API-клиент с JWT из localStorage
const TOKEN_KEY = 'sk_token';

export function getToken(): string | null { return localStorage.getItem(TOKEN_KEY); }
export function setToken(t: string) { localStorage.setItem(TOKEN_KEY, t); }
export function clearToken() { localStorage.removeItem(TOKEN_KEY); }

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
};

// ---- Типы ----
export type TxType = 0 | 1; // 0 = доход, 1 = расход

export interface UserInfo { id: number; email: string; name: string; startingBalance?: number; }
export interface Category { id: number; name: string; type: TxType; color: string; isSystem?: boolean; }
export interface Tx {
  id: number; categoryId: number; type: TxType; amount: number; date: string;
  comment: string; categoryName?: string; categoryColor?: string;
  accountId?: number | null; accountName?: string | null;
}
export type AccountType = 0 | 1 | 2 | 3 | 4; // дебет, кредит, наличные, вклад, прочее
export interface AccountInfo {
  id: number; name: string; type: AccountType; typeName: string; balance: number; startingBalance: number;
  creditLimit: number; cashbackRate: number; cashbackAccrued: number; color: string;
  isArchived: boolean; available: number; creditUsagePct: number | null;
}
export interface FamilyMember { id: number; name: string; email: string; }
export interface Goal {
  id: number; name: string; targetAmount: number; currentAmount: number;
  deadline: string | null; progress: number; monthsLeft: number | null; perMonth: number | null;
}
export interface LimitRow { id: number; categoryId: number; limit: number; name: string; color: string; spent: number; }
export interface ShareRow { categoryId: number; name: string; color: string; total: number; }
export interface MonthRow { year: number; month: number; income: number; expense: number; }
export interface Recommendation { level: 'danger' | 'warn' | 'info' | 'good'; icon: string; text: string; }
export interface Summary {
  year: number; month: number; startingBalance: number;
  balance: number; monthIncome: number; monthExpense: number; totalBalance: number; savingsRate: number;
  byMonth: MonthRow[]; expenseShare: ShareRow[]; limits: LimitRow[]; goals: Goal[];
  accounts: Pick<AccountInfo, 'id'|'name'|'type'|'typeName'|'balance'|'creditLimit'|'cashbackRate'|'cashbackAccrued'|'color'>[];
  cashbackTotal: number;
  recommendations: Recommendation[];
}

export const fmt = (n: number) => new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 0 }).format(n) + ' ₽';
export const MONTHS_RU = ['Январь','Февраль','Март','Апрель','Май','Июнь','Июль','Август','Сентябрь','Октябрь','Ноябрь','Декабрь'];
