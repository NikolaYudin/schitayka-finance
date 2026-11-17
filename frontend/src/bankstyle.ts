// Фирменный стиль карточек счёта по банку (0.3/N6).
// Выбор хранится локально: 'neutral' (по умолчанию) | 'brand'.
// Цвета — из справочника банков (Banks.BrandColor); логотипов-ссылок нет:
// монограмма рисуется из ShortName, чтобы приложение работало офлайн.

export const BANK_STYLE_KEY = 'sk_bankstyle';
export type BankCardStyle = 'neutral' | 'brand';

export function getBankCardStyle(): BankCardStyle {
  try { return localStorage.getItem(BANK_STYLE_KEY) === 'brand' ? 'brand' : 'neutral'; }
  catch { return 'neutral'; }
}

export function setBankCardStyle(v: BankCardStyle) {
  try { localStorage.setItem(BANK_STYLE_KEY, v); } catch { /* ignore */ }
}

export interface BankedAccount {
  bankShortName?: string | null;
  bankColor?: string | null;
}

/** Данные фирменного стиля для карточки: null — рисуем нейтрально. */
export function bankOf(a: BankedAccount): { color: string; short: string | null } | null {
  if (getBankCardStyle() !== 'brand' || !a.bankColor) return null;
  return { color: a.bankColor, short: (a.bankShortName || '').trim() || null };
}

/** Затемнение цвета (смешение с чёрным) для второй точки градиента. */
export function darken(hex: string, k = 0.4): string {
  const m = /^#([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return hex;
  const n = parseInt(m[1], 16);
  const f = (v: number) => Math.max(0, Math.round(v * (1 - k)));
  const r = f((n >> 16) & 255), g = f((n >> 8) & 255), b = f(n & 255);
  return `#${((1 << 24) + (r << 16) + (g << 8) + b).toString(16).slice(1)}`;
}

/** Монограмма банка: первая буква краткого имени или названия. */
export function bankMonogram(short: string | null, name?: string | null): string {
  const src = (short || name || '?').trim();
  return (src[0] ?? '?').toUpperCase();
}
