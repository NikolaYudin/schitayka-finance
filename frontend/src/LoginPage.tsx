import { useEffect, useState } from 'react';
import { useAuth } from './auth';
import { api } from './api';
import { Icon } from './icons';

// Флаг включения кнопки «Войти через Яндекс».
// Сейчас: false (используется только email + пароль).
// Чтобы вернуть: true здесь + "Enabled": true в YandexOAuth (backend/appsettings.json).
const ENABLE_YANDEX = false;

export default function LoginPage({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [demoEnabled, setDemoEnabled] = useState<boolean | null>(null);

  useEffect(() => {
    api.get<{ enabled: boolean }>('/api/auth/demo-status')
      .then(r => setDemoEnabled(!!r.enabled))
      .catch(() => setDemoEnabled(null));
  }, []);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(''); setBusy(true);
    try {
      await auth.login(email, password);
      location.hash = '#/';
    } catch (err: any) {
      setError(err.message || 'Не удалось войти');
    } finally { setBusy(false); }
  };

  return (
    <div className="auth-wrap">
      <span className="auth-blob b1" aria-hidden="true" />
      <span className="auth-blob b2" aria-hidden="true" />
      <span className="auth-blob b3" aria-hidden="true" />

      <div className="card auth-card">
        <div className="auth-brand">
          <div className="brand-mark"><Icon name="calculator" size={26} /></div>
          <div>
            <div className="auth-logo">СчитайКа</div>
            <div className="auth-sub">Учёт доходов и расходов, анализ и цели</div>
          </div>
        </div>

        <form onSubmit={submit}>
          <label>Email
            <input type="email" value={email} onChange={e => setEmail(e.target.value)} placeholder="you@mail.ru" required autoComplete="email" />
          </label>
          <label>Пароль
            <input
              type="password"
              value={password}
              onChange={e => setPassword(e.target.value)}
              placeholder="Введите пароль"
              minLength={6}
              required
              autoComplete="current-password"
            />
          </label>

          {error && <div className="alert alert-danger">{error}</div>}

          <button className="btn primary block" style={{ height: 46, marginTop: 6 }} disabled={busy}>
            {busy ? 'Подождите…' : 'Войти'}
            {!busy && <Icon name="chevron-right" size={16} />}
          </button>
        </form>

        {ENABLE_YANDEX && (
          <>
            <div className="auth-demo" style={{ justifyContent: 'center' }}>или</div>
            <button className="btn secondary block" style={{ marginTop: 10 }} onClick={auth.yandexLogin} type="button">
              <span className="member-avatar" style={{ width: 24, height: 24, flex: '0 0 24px', fontSize: 11 }}>Я</span>
              Войти через Яндекс
            </button>
          </>
        )}

        {demoEnabled === true && (
          <div className="auth-demo">
            <Icon name="shield" size={15} />
            <span>Демо-доступ: <b>demo@schitayka.ru</b> / <b>demo123</b></span>
          </div>
        )}
        {demoEnabled === false && (
          <div className="auth-demo">
            <Icon name="user" size={15} />
            <span>Регистрация отключена. Войдите с существующей учётной записью.</span>
          </div>
        )}
      </div>
    </div>
  );
}
