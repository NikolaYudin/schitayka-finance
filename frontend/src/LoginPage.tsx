import { useEffect, useState } from 'react';
import { useAuth } from './auth';
import { api } from './api';
import { Icon } from './icons';

// Флаг включения кнопки «Войти через Яндекс».
// Сейчас: false (используется только email + пароль).
// Чтобы вернуть: true здесь + "Enabled": true в YandexOAuth (backend/appsettings.json).
const ENABLE_YANDEX = false;

export default function LoginPage({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [inviteCode, setInviteCode] = useState('');
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
      if (mode === 'login') await auth.login(email, password);
      else await auth.register(name, email, password, inviteCode);
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

        <div className="tabs">
          <button type="button" className={`tab ${mode === 'login' ? 'active' : ''}`} onClick={() => setMode('login')}>
            Вход
          </button>
          <button type="button" className={`tab ${mode === 'register' ? 'active' : ''}`} onClick={() => setMode('register')}>
            Регистрация
          </button>
        </div>

        <form onSubmit={submit}>
          {mode === 'register' && (
            <label>Имя
              <input value={name} onChange={e => setName(e.target.value)} placeholder="Анна" required />
            </label>
          )}
          <label>Email
            <input type="email" value={email} onChange={e => setEmail(e.target.value)} placeholder="you@mail.ru" required autoComplete="email" />
          </label>
          <label>Пароль
            <input
              type="password"
              value={password}
              onChange={e => setPassword(e.target.value)}
              placeholder="Минимум 6 символов"
              minLength={6}
              required
              autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
            />
          </label>
          {mode === 'register' && (
            <label>Код семьи (необязательно)
              <input
                value={inviteCode}
                onChange={e => setInviteCode(e.target.value.toUpperCase())}
                placeholder="напр. FAM2026"
                maxLength={12}
                style={{ textTransform: 'uppercase', letterSpacing: 2 }}
              />
            </label>
          )}

          {error && <div className="alert alert-danger">{error}</div>}

          <button className="btn primary block" style={{ height: 46, marginTop: 6 }} disabled={busy}>
            {busy ? 'Подождите…' : mode === 'login' ? 'Войти' : 'Создать аккаунт'}
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
            <span>Первый запуск? Создайте аккаунт во вкладке <b>«Регистрация»</b>.</span>
          </div>
        )}
      </div>
    </div>
  );
}
