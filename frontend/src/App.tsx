import { useEffect, useState } from 'react';
import { useAuth } from './auth';
import { api, Category, AccountInfo } from './api';
import LoginPage from './LoginPage';
import { Dashboard, Journal, QuickTxModal } from './components';
import { GoalsPage } from './GoalsPage';
import { AccountsPage } from './AccountsPage';
import { FamilyCard } from './AccountsPage';
import { useTheme, type ThemeController, type ThemeMode } from './theme';
import { ToastProvider } from './toasts';
import { AccountRulesCard } from './AccountRulesCard';

type Route = '' | 'journal' | 'accounts' | 'goals' | 'settings' | 'login';
function useHashRoute(): [Route, (r: Route) => void] {
  const parse = (): Route => {
    const h = location.hash.replace(/^#\/?/, '').split('?')[0];
    return (h === 'journal' || h === 'accounts' || h === 'goals' || h === 'settings' || h === 'login') ? h : '';
  };
  const [route, setRoute] = useState<Route>(parse);
  useEffect(() => {
    const on = () => setRoute(parse());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return [route, r => { location.hash = r ? `#/${r}` : '#/'; }];
}

export default function App() {
  const auth = useAuth();
  const theme = useTheme();
  const [route, go] = useHashRoute();
  const [showQuick, setShowQuick] = useState(false);
  const [cats, setCats] = useState<Category[]>([]);
  const [accounts, setAccounts] = useState<AccountInfo[]>([]);
  const [tick, setTick] = useState(0); // перезагрузка дашборда после сохранения

  // Категории и счета для быстрой операции
  useEffect(() => {
    if (auth.user) {
      api.get<Category[]>('/api/categories').then(setCats).catch(() => {});
      api.get<AccountInfo[]>('/api/accounts').then(setAccounts).catch(() => {});
    }
  }, [auth.user, tick]);

  // Навигация — только в эффекте, а не в теле рендера (иначе side effect при каждом рендере).
  useEffect(() => {
    if (auth.loading) return;
    if (!auth.user) {
      if (route !== 'login') go('login');
    } else if (route === 'login') {
      go('');
    }
  }, [auth.loading, auth.user, route]);

  if (auth.loading) return <div className="app"><div className="card muted">Проверка сессии…</div></div>;
  if (!auth.user) return <LoginPage auth={auth} />;

  return (
    <ToastProvider>
    <div className="app">
      <header className="topbar">
        <h1 className="logo">🧮 СчитайКа</h1>
        <div className="topbar-actions">
          <span className="muted">личный учёт финансов</span>
          <button
            type="button"
            className="theme-toggle"
            onClick={theme.toggle}
            title={theme.resolved === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему'}
            aria-label={theme.resolved === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему'}
          >
            {theme.resolved === 'dark' ? '☀️' : '🌙'}
          </button>
        </div>
      </header>

      {route === 'journal' ? <Journal key={tick} />
        : route === 'accounts' ? <AccountsPage key={tick} onChanged={() => setTick(t => t + 1)} />
        : route === 'goals' ? <GoalsPage key={tick} />
        : route === 'settings' ? <Settings auth={auth} categories={cats} theme={theme} onReload={() => setTick(t => t + 1)} />
        : <Dashboard key={tick} />}

      {/* FAB доступна на основных экранах */}
      {route !== 'settings' && (
        <button className="fab" title="Добавить операцию" onClick={() => setShowQuick(true)}>＋</button>
      )}
      {showQuick && (
        <QuickTxModal categories={cats} accounts={accounts}
          onClose={() => setShowQuick(false)} onSaved={() => setTick(t => t + 1)} />
      )}

      <nav className="bottom-nav">
        <a href="#/" className={route === '' ? 'active' : ''} onClick={() => go('')}>📊 Обзор</a>
        <a href="#/accounts" className={route === 'accounts' ? 'active' : ''} onClick={() => go('accounts')}>💳 Счета</a>
        <a href="#/journal" className={route === 'journal' ? 'active' : ''} onClick={() => go('journal')}>🗂 Журнал</a>
        <a href="#/goals" className={route === 'goals' ? 'active' : ''} onClick={() => go('goals')}>🎯 Цели</a>
        <a href="#/settings" className={route === 'settings' ? 'active' : ''} onClick={() => go('settings')}>⚙️ Настройки</a>
      </nav>
      </div>
    </ToastProvider>
  );
}

function Settings({ auth, categories, theme, onReload }: {
  auth: ReturnType<typeof useAuth>; categories: Category[]; theme: ThemeController; onReload: () => void;
}) {
  return (
    <>
      <div className="card">
        <h3>⚙️ Аккаунт</h3>
        <table>
          <tbody>
            <tr><td><b>Имя</b></td><td>{auth.user?.name}</td></tr>
            <tr><td><b>Email</b></td><td>{auth.user?.email}</td></tr>
          </tbody>
        </table>
        <StartingBalance auth={auth} />
        <button className="btn danger-ghost" onClick={auth.logout}>Выйти</button>
      </div>
      <AppearanceCard theme={theme} />
      <CategoriesCard categories={categories} onChanged={() => { onReload(); auth.refresh(); }} />
      <FamilyCard />
      <AccountRulesCard />
    </>
  );
}

// Оформление: светлая / тёмная / системная тема
function AppearanceCard({ theme }: { theme: ThemeController }) {
  const options: { value: ThemeMode; icon: string; label: string }[] = [
    { value: 'light', icon: '☀️', label: 'Светлая' },
    { value: 'dark', icon: '🌙', label: 'Тёмная' },
    { value: 'system', icon: '🖥️', label: 'Системная' },
  ];
  return (
    <div className="card">
      <h3>🎨 Оформление</h3>
      <div className="muted">Тема применяется сразу и запоминается на этом устройстве.</div>
      <div className="segmented">
        {options.map(o => (
          <button
            key={o.value}
            type="button"
            className={`btn ${theme.mode === o.value ? 'active' : ''}`}
            onClick={() => theme.setMode(o.value)}
          >
            {o.icon} {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}

// Стартовый баланс — «сколько денег на счетах сейчас», влияет на итоговый баланс в Обзоре
function StartingBalance({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [val, setVal] = useState(String(auth.user?.startingBalance ?? 0));
  const [msg, setMsg] = useState('');
  const save = async (e: React.FormEvent) => {
    e.preventDefault(); setMsg('');
    try {
      await api.post('/api/budget/start-balance', { startingBalance: Number(val.replace(',', '.')) || 0 });
      setMsg('Сохранено ✓'); auth.refresh();
    } catch (ex: any) { setMsg(ex.message); }
  };
  return (
    <form onSubmit={save} style={{ marginTop: 10 }}>
      <label>Стартовый баланс на счетах, ₽
        <input inputMode="decimal" value={val} onChange={e => setVal(e.target.value)} placeholder="0" />
      </label>
      {msg && <div className="muted">{msg}</div>}
      <button className="btn primary small">Сохранить</button>
    </form>
  );
}

// Управление категориями (личные справочники)
function CategoriesCard({ categories, onChanged }: { categories: Category[]; onChanged: () => void }) {
  const [name, setName] = useState('');
  const [type, setType] = useState<0 | 1>(1);
  const [color, setColor] = useState('#2b6cb0');
  const [err, setErr] = useState('');

  const add = async (e: React.FormEvent) => {
    e.preventDefault(); setErr('');
    try {
      await api.post('/api/categories', { name, type, color });
      setName(''); onChanged();
    } catch (ex: any) { setErr(ex.message); }
  };
  const del = async (id: number) => {
    if (!confirm('Удалить категорию?')) return;
    try { await api.del(`/api/categories/${id}`); onChanged(); }
    catch (ex: any) { alert(ex.message); }
  };

  return (
    <div className="card">
      <h3>🏷 Категории</h3>
      <table>
        <thead><tr><th>Категория</th><th>Тип</th><th></th></tr></thead>
        <tbody>
          {categories.map(c => (
            <tr key={c.id}>
              <td><b style={{ color: c.color }}>●</b> {c.name}</td>
              <td>{c.type === 0 ? 'доход' : 'расход'}</td>
              <td><button className="btn danger-ghost small" onClick={() => del(c.id)}>✕</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <form onSubmit={add} style={{ marginTop: 12 }}>
        <div className="form-row">
          <label>Название<input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Бензин" /></label>
          <label>Тип
            <select value={type} onChange={e => setType(Number(e.target.value) as 0 | 1)}>
              <option value={1}>Расход</option><option value={0}>Доход</option>
            </select>
          </label>
          <label>Цвет<input type="color" value={color} onChange={e => setColor(e.target.value)} style={{ height: 40, padding: 2 }} /></label>
        </div>
        {err && <div className="alert">{err}</div>}
        <button className="btn primary">+ Добавить категорию</button>
      </form>
    </div>
  );
}
