/**
 * Хранилище JWT (B17). Токен живёт в sessionStorage — после закрытия вкладки
 * сессия заканчивается, а XSS не может достать его из постоянного localStorage.
 * Если хранилище недоступно (приватный режим), используем память вкладки.
 */
const TOKEN_KEY = 'sk_token';
let memoryToken: string | null = null;

function store(): Storage | null {
  try { return sessionStorage; } catch { return null; }
}

export function getToken(): string | null {
  const s = store();
  if (!s) return memoryToken;
  try { return s.getItem(TOKEN_KEY) ?? memoryToken; } catch { return memoryToken; }
}

export function setToken(token: string): void {
  memoryToken = token;
  const s = store();
  if (!s) return;
  try { s.setItem(TOKEN_KEY, token); } catch { /* ignore */ }
}

export function clearToken(): void {
  memoryToken = null;
  const s = store();
  if (!s) return;
  try { s.removeItem(TOKEN_KEY); } catch { /* ignore */ }
}
