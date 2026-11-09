import { useCallback, useEffect, useState } from 'react';

export type ThemeMode = 'light' | 'dark' | 'system';
export type ResolvedTheme = 'light' | 'dark';

const STORAGE_KEY = 'sk_theme';
const DARK_QUERY = '(prefers-color-scheme: dark)';
const THEME_COLORS: Record<ResolvedTheme, string> = { light: '#1a365d', dark: '#0f172a' };

export function getStoredTheme(): ThemeMode {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    if (v === 'light' || v === 'dark' || v === 'system') return v;
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
  document.documentElement.style.colorScheme = resolved;
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', THEME_COLORS[resolved]);
  return resolved;
}

// Режим темы: light | dark | system. Выбор хранится в localStorage,
// системный режим следит за prefers-color-scheme.
export function useTheme() {
  const [mode, setMode] = useState<ThemeMode>(() => getStoredTheme());
  const [resolved, setResolved] = useState<ResolvedTheme>(() => resolveTheme(getStoredTheme()));

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

  const toggle = useCallback(() => {
    setMode(prev => (resolveTheme(prev) === 'dark' ? 'light' : 'dark'));
  }, []);

  return { mode, resolved, setMode, toggle };
}

export type ThemeController = ReturnType<typeof useTheme>;
