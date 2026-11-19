import { useEffect, useState } from 'react';
import { useAuth } from './auth';
import { api, setToken } from './api';
import { Icon } from './icons';

// Флаг включения кнопки «Войти через Яндекс».
// Сейчас: false (вход по email и паролю, новые записи создаёт администратор).
const ENABLE_YANDEX = false;

export default function LoginPage({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [demoEnabled, setDemoEnabled] = useState<boolean | null>(null);
  const [setupRequired, setSetupRequired] = useState<boolean | null>(null);

  // Поля первичной настройки: мастер-профиль в БД передаёт права первому пользователю.
  const [adminEmail, setAdminEmail] = useState('');
  const [adminName, setAdminName] = useState('');
  const [adminPassword, setAdminPassword] = useState('');

  useEffect(() => {
    api.get<{ enabled: boolean }>('/api/auth/demo-status')
      .then(r => setDemoEnabled(!!r.enabled))
      .catch(() => setDemoEnabled(null));
    api.get<{ required: boolean }>('/api/setup/status')
      .then(r => setSetupRequired(!!r.required))
      .catch(() => setSetupRequired(false));
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

  const setup = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(''); setBusy(true);
    try {
      const r = await api.post<{ token: string }>('/api/setup/admin', {
        email: adminEmail, name: adminName, password: adminPassword,
      });
      setToken(r.token);
      await auth.refresh();
      location.hash = '#/';
    } catch (err: any) {
      setError(err.message || 'Не удалось создать администратора');
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

        {setupRequired ? (
          <form onSubmit={setup}>
            <h3 style={{ marginTop: 0 }}>Первый запуск</h3>
            <p className="card-sub">
              Создайте администратора — мастер-профиль передаст ему все права.
              Позже администратор сможет завести пользователей для семьи.
            </p>
            <label>Email администратора
              <input type="email" value={adminEmail} onChange={e => setAdminEmail(e.target.value)}
                placeholder="you@mail.ru" required autoComplete="email" />
            </label>
            <label>Имя
              <input value={adminName} onChange={e => setAdminName(e.target.value)}
                placeholder="Как вас зовут?" required />
            </label>
            <label>Пароль (не короче 8 символов)
              <input type="password" value={adminPassword} onChange={e => setAdminPassword(e.target.value)}
                minLength={8} required autoComplete="new-password" />
            </label>
            {error && <div className="alert alert-danger">{error}</div>}
            <button className="btn primary block" style={{ height: 46, marginTop: 6 }} disabled={busy}>
              {busy ? 'Создаём…' : 'Создать администратора'}
            </button>
          </form>
        ) : (
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
        )}

        {ENABLE_YANDEX && !setupRequired && (
          <>
            <div className="auth-demo" style={{ justifyContent: 'center' }}>или</div>
            <button className="btn secondary block" style={{ marginTop: 10 }} onClick={auth.yandexLogin} type="button">
              <span className="member-avatar" style={{ width: 24, height: 24, flex: '0 0 24px', fontSize: 11 }}>Я</span>
              Войти через Яндекс
            </button>
          </>
        )}

        {setupRequired === false && demoEnabled === true && (
          <div className="auth-demo">
            <Icon name="shield" size={15} />
            <span>Демо-доступ: <b>demo@schitayka.ru</b> / <b>demo123</b></span>
          </div>
        )}
        {setupRequired === false && demoEnabled === false && (
          <div className="auth-demo">
            <Icon name="user" size={15} />
            <span>Учётные записи создаёт администратор семьи.</span>
          </div>
        )}
      </div>
    </div>
  );
}
