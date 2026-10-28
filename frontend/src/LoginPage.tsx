import { useState } from 'react';
import { useAuth } from './auth';

// Флаг включения кнопки «Войти через Яндекс».
// Сейчас: false (используется только email + пароль).
// Чтобы вернуть: true здесь + "Enabled": true в YandexOAuth (backend/appsettings.json).
const ENABLE_YANDEX = false;

export default function LoginPage({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [mode, setMode] = useState<'login' | 'register'>('login');
  const [name, setName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(''); setBusy(true);
    try {
      if (mode === 'login') await auth.login(email, password);
      else await auth.register(name, email, password);
      location.hash = '#/';
    } catch (err: any) {
      setError(err.message || 'Ошибка');
    } finally { setBusy(false); }
  };

  return (
    <div className="auth-wrap">
      <div className="card auth-card">
        <h1 className="logo">🧮 СчитайКа</h1>
        <p className="muted">Учёт доходов и расходов, анализ бюджета и финансовые цели</p>

        <div className="tabs">
          <button className={mode === 'login' ? 'tab active' : 'tab'} onClick={() => setMode('login')}>Вход</button>
          <button className={mode === 'register' ? 'tab active' : 'tab'} onClick={() => setMode('register')}>Регистрация</button>
        </div>

        <form onSubmit={submit}>
          {mode === 'register' && (
            <label>Имя
              <input value={name} onChange={e => setName(e.target.value)} placeholder="Анна" required />
            </label>
          )}
          <label>Email
            <input type="email" value={email} onChange={e => setEmail(e.target.value)} placeholder="you@mail.ru" required />
          </label>
          <label>Пароль
            <input type="password" value={password} onChange={e => setPassword(e.target.value)} minLength={6} required />
          </label>
          {error && <div className="alert">{error}</div>}
          <button className="btn primary block" disabled={busy}>
            {busy ? '…' : mode === 'login' ? 'Войти' : 'Создать аккаунт'}
          </button>
        </form>

        {/* Яндекс OAuth временно отключён (ENABLE_YANDEX = false).
            Код auth.yandexLogin и бэкенд-эндпоинты на месте — включите флаг здесь
            и YandexOAuth:Enabled=true в appsettings.json, чтобы вернуть кнопку. */}
        {ENABLE_YANDEX && (
          <>
            <div className="divider">или</div>
            <button className="btn yandex block" onClick={auth.yandexLogin}>
              <span className="ya-dot">Я</span> Войти через Яндекс
            </button>
          </>
        )}

        <p className="muted" style={{ marginTop: 14 }}>Демо-доступ: demo@schitayka.ru / demo123</p>
      </div>
    </div>
  );
}
