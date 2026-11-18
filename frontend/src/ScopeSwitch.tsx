import { useEffect, useState } from 'react';
import { loadScope, saveScope, SCOPE_CHANGED, type ScopeState } from './scope';

/**
 * Общий переключатель охвата «я / семья / выбранные».
 * Используется в долгах, платежах и календаре; состояние общее с дашбордом (sk_scope).
 */
export function ScopeSwitch() {
  const [scope, setScope] = useState<ScopeState>(loadScope);

  useEffect(() => {
    const sync = () => setScope(loadScope());
    window.addEventListener(SCOPE_CHANGED, sync);
    return () => window.removeEventListener(SCOPE_CHANGED, sync);
  }, []);

  const change = (mode: ScopeState['mode']) => {
    const next: ScopeState = { ...scope, mode };
    setScope(next);
    saveScope(next);
  };

  const modes: { v: ScopeState['mode']; label: string }[] = [
    { v: 'me', label: 'Я' },
    { v: 'family', label: 'Вся семья' },
  ];
  if (scope.mode === 'ids') modes.push({ v: 'ids', label: `Выбранные (${scope.ids.length})` });

  return (
    <div className="segmented" style={{ maxWidth: 300 }} aria-label="Охват данных">
      {modes.map(m => (
        <button
          key={m.v} type="button"
          className={`segment ${scope.mode === m.v ? 'active' : ''}`}
          onClick={() => change(m.v)}
        >
          {m.label}
        </button>
      ))}
    </div>
  );
}
