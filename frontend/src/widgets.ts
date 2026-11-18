// Настройки видимости виджетов дашборда (пункты 1.2, 2, 3 и 5 плана).
// Хранятся локально, как тема и охват дашборда; на сервер не отправляются.
const KEY = 'sk_dashboard_widgets';

export interface DashboardWidgets
{
  investments: boolean;
  debts: boolean;
  obligations: boolean;
  calendar: boolean;
}

export const DEFAULT_WIDGETS: DashboardWidgets = {
  investments: true, debts: true, obligations: true, calendar: true,
};

export function loadWidgets(): DashboardWidgets
{
  try
  {
    const raw = localStorage.getItem(KEY);
    if (raw)
    {
      const v = JSON.parse(raw);
      return {
        investments: typeof v?.investments === 'boolean' ? v.investments : DEFAULT_WIDGETS.investments,
        debts: typeof v?.debts === 'boolean' ? v.debts : DEFAULT_WIDGETS.debts,
        obligations: typeof v?.obligations === 'boolean' ? v.obligations : DEFAULT_WIDGETS.obligations,
        calendar: typeof v?.calendar === 'boolean' ? v.calendar : DEFAULT_WIDGETS.calendar,
      };
    }
  }
  catch { /* приватный режим и т.п. */ }
  return { ...DEFAULT_WIDGETS };
}

export function saveWidgets(w: DashboardWidgets)
{
  try { localStorage.setItem(KEY, JSON.stringify(w)); } catch { /* ignore */ }
  // Дашборд подписывается на это событие и перерисовывает состав виджетов.
  window.dispatchEvent(new Event('sk-widgets-changed'));
}
