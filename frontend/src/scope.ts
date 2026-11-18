import type { DashboardScope } from './api';

/** Общий охват «я / семья / выбранные»: используется дашбордом и разделами. */
const SCOPE_KEY = 'sk_scope';
export const SCOPE_CHANGED = 'sk-scope-changed';

export interface ScopeState {
  mode: DashboardScope;
  ids: number[];
}

export function loadScope(): ScopeState {
  try {
    const raw = localStorage.getItem(SCOPE_KEY);
    if (raw) {
      const v = JSON.parse(raw);
      if (v && ['me', 'family', 'ids'].includes(v.mode)) {
        return {
          mode: v.mode,
          ids: Array.isArray(v.ids) ? v.ids.filter((n: unknown) => typeof n === 'number') : [],
        };
      }
    }
  } catch { /* приватный режим и т.п. */ }
  return { mode: 'me', ids: [] };
}

export function saveScope(s: ScopeState): void {
  try { localStorage.setItem(SCOPE_KEY, JSON.stringify(s)); } catch { /* ignore */ }
  window.dispatchEvent(new Event(SCOPE_CHANGED));
}

/** Строка запроса для API (me — без параметров). */
export function scopeQuery(s: ScopeState): string {
  if (s.mode === 'me') return '';
  const p = new URLSearchParams();
  p.set('scope', s.mode);
  if (s.mode === 'ids' && s.ids.length > 0) p.set('ids', s.ids.join(','));
  const q = p.toString();
  return q ? `?${q}` : '';
}

/** Добавить охват к уже существующему URL с параметрами. */
export function withScope(url: string, s: ScopeState): string {
  if (s.mode === 'me') return url;
  const q = scopeQuery(s).slice(1);
  return url.includes('?') ? `${url}&${q}` : `${url}?${q}`;
}
