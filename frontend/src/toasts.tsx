import { createContext, useCallback, useContext, useState, type ReactNode } from 'react';

export type ToastLevel = 'info' | 'warn' | 'danger';

interface Toast { id: number; level: ToastLevel; text: string; }

const ToastCtx = createContext<(level: ToastLevel, text: string) => void>(() => { });

export const useToast = () => useContext(ToastCtx);

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const dismiss = useCallback((id: number) => setToasts(t => t.filter(x => x.id !== id)), []);

  const push = useCallback((level: ToastLevel, text: string) => {
    const id = Date.now() + Math.random();
    setToasts(t => [...t.slice(-3), { id, level, text }]);
    window.setTimeout(() => dismiss(id), 6500);
  }, [dismiss]);

  return (
    <ToastCtx.Provider value={push}>
      {children}
      <div className="toast-host">
        {toasts.map(t => (
          <div key={t.id} className={`toast toast-${t.level}`} onClick={() => dismiss(t.id)}>
            <span>{t.text}</span>
            <button type="button" className="toast-close" aria-label="закрыть">✕</button>
          </div>
        ))}
      </div>
    </ToastCtx.Provider>
  );
}
