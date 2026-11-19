import { useCallback, useEffect, useRef, useState } from 'react';
import { api, getToken } from './api';

export type ThemeMode = 'light' | 'dark' | 'system';
export type ResolvedTheme = 'light' | 'dark';

const STORAGE_KEY = 'sk_theme';
const DARK_QUERY = '(prefers-color-scheme: dark)';
const THEME_COLORS: Record<ResolvedTheme, string> = { light: '#f1f2f7', dark: '#09090b' };

function isThemeMode(v: unknown): v is ThemeMode {
  return v === 'light' || v === 'dark' || v === 'system';
}

export function getStoredTheme(): ThemeMode {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    if (isThemeMode(v)) return v;
  } catch { /* localStorage может быть недоступен */ }
  return 'system';
}

export function resolveTheme(mode: ThemeMode): ResolvedTheme {
  if (mode !== 'system') return mode;
  return window.matchMedia?.(DARK_QUERY).matches ? 'dark' : 'light';
}

export function applyTheme(mode: ThemeMode): ResolvedTheme {
  const resolved = resolveTheme(mode);
  document.documentElement.dataset.theme = resolved;
  document.documentElement.dataset.bsTheme = resolved;
  document.documentElement.style.colorScheme = resolved;
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', THEME_COLORS[resolved]);
  return resolved;
}

/**
 * Режим темы: light | dark | system.
 * B6: localStorage остаётся офлайн-кэшем, но после загрузки профиля значение
 * приходит с сервера; каждое изменение отправляется на сервер (best effort).
 * Если сервер недоступен — локальный выбор продолжает работать.
 */
export function useTheme(serverTheme?: ThemeMode | null) {
  const [mode, setModeState] = useState<ThemeMode>(() => getStoredTheme());
  const [resolved, setResolved] = useState<ResolvedTheme>(() => resolveTheme(getStoredTheme()));
  const hydrated = useRef(false);

  // Серверное значение применяется один раз за жизнь компонента: при логине
  // оно переопределяет кэш этого браузера.
  useEffect(() => {
    if (!isThemeMode(serverTheme) || hydrated.current) return;
    hydrated.current = true;
    if (serverTheme !== getStoredTheme()) setModeState(serverTheme);
  }, [serverTheme]);

  useEffect(() => {
    setResolved(applyTheme(mode));
    try { localStorage.setItem(STORAGE_KEY, mode); } catch { /* ignore */ }

    const mq = window.matchMedia?.(DARK_QUERY);
    if (!mq) return;
    const onChange = () => {
      if (mode === 'system') setResolved(applyTheme('system'));
    };
    mq.addEventListener?.('change', onChange);
    return () => mq.removeEventListener?.('change', onChange);
  }, [mode]);

  const setMode = useCallback((next: ThemeMode) => {
    setModeState(next);
    if (!getToken()) return; // не авторизован — только локальный кэш
    void api.put('/api/settings/appearance', { theme: next }).catch(() => {
      // Ошибка синхронизации не должна ломать переключение темы.
    });
  }, []);

  const toggle = useCallback(() => {
    setMode(resolveTheme(mode) === 'dark' ? 'light' : 'dark');
  }, [mode, setMode]);

  return { mode, resolved, setMode, toggle };
}

export type ThemeController = ReturnType<typeof useTheme>;
