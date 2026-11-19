import { createContext, useCallback, useContext, useState, type ReactNode } from 'react';

/**
 * Единые диалоги вместо нативных alert/confirm/prompt.
 * Вызов возвращает Promise, поэтому handlers должны быть async.
 */
export interface DialogApi {
  confirm(message: string, title?: string): Promise<boolean>;
  alert(message: string, title?: string): Promise<void>;
  prompt(message: string, defaultValue?: string, title?: string): Promise<string | null>;
}

type Kind = 'confirm' | 'alert' | 'prompt';
interface Request {
  kind: Kind;
  message: string;
  title: string;
  defaultValue: string;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  resolve: (value: any) => void;
}

const DialogCtx = createContext<DialogApi>({
  confirm: async () => false,
  alert: async () => { },
  prompt: async () => null,
});

export const useDialog = () => useContext(DialogCtx);

export function DialogProvider({ children }: { children: ReactNode }) {
  const [current, setCurrent] = useState<Request | null>(null);
  const [value, setValue] = useState('');

  const open = useCallback((req: Omit<Request, 'defaultValue'> & { defaultValue?: string }) => {
    setCurrent({ defaultValue: '', ...req });
    setValue(req.defaultValue ?? '');
  }, []);

  const close = useCallback((result: boolean | string | null | void) => {
    current?.resolve(result);
    setCurrent(null);
    setValue('');
  }, [current]);

  const api: DialogApi = {
    confirm: (message, title = 'Подтверждение') => new Promise(resolve =>
      open({ kind: 'confirm', message, title, resolve })) as Promise<boolean>,
    alert: (message, title = 'Сообщение') => new Promise(resolve =>
      open({ kind: 'alert', message, title, resolve })) as Promise<void>,
    prompt: (message, defaultValue = '', title = 'Ввод') => new Promise(resolve =>
      open({ kind: 'prompt', message, title, defaultValue, resolve })) as Promise<string | null>,
  };

  return (
    <DialogCtx.Provider value={api}>
      {children}
      {current && (
        <div className="dlg-backdrop" onClick={() => close(null)}>
          <div className="dlg" role="dialog" aria-modal="true" onClick={e => e.stopPropagation()}>
            <div className="dlg-head">
              <strong>{current.title}</strong>
              <button type="button" className="btn icon" aria-label="Закрыть" onClick={() => close(null)}>×</button>
            </div>
            <p className="dlg-msg">{current.message}</p>
            {current.kind === 'prompt' && (
              <input
                className="dlg-input" autoFocus value={value}
                onChange={e => setValue(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') close(value); }}
              />
            )}
            <div className="dlg-actions">
              {current.kind !== 'alert' && (
                <button type="button" className="btn secondary" onClick={() => close(null)}>Отмена</button>
              )}
              <button
                type="button" className={`btn ${current.kind === 'alert' ? 'primary' : 'primary'}`}
                onClick={() => close(current.kind === 'prompt' ? value : true)}
              >
                {current.kind === 'prompt' ? 'Ок' : 'Ок'}
              </button>
            </div>
          </div>
        </div>
      )}
    </DialogCtx.Provider>
  );
}
