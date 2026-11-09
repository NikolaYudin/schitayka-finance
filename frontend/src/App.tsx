import { useEffect, useState } from 'react';
import { useAuth } from './auth';
import { api, Category, AccountInfo } from './api';
import LoginPage from './LoginPage';
import { Dashboard, Journal, QuickTxModal } from './components';
import { GoalsPage } from './GoalsPage';
import { AccountsPage, FamilyCard } from './AccountsPage';
import { AccountRulesCard } from './AccountRulesCard';
import { useTheme, type ThemeController, type ThemeMode } from './theme';
import { ToastProvider } from './toasts';

type Route = '' | 'journal' | 'accounts' | 'goals' | 'settings' | 'login';

function useHashRoute(): [Route, (r: Route) => void] {
  const parse = (): Route => {
    const h = location.hash.replace(/^#\/?/, '').split('?')[0];
    return (h === 'journal' || h === 'accounts' || h === 'goals' || h === 'settings' || h === 'login') ? h : '';
  };
  const [route, setRouteState] = useState<Route>(parse);
  useEffect(() => {
    const on = () => setRouteState(parse());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  const setRoute = (r: Route) => { location.hash = r ? `#/${r}` : '#/'; };
  return [route, setRoute];
}

// ---------- Боковая панель навигации ----------
function NavIcon({ d }: { d: string }) {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={d} />
    </svg>
  );
}

function Sidebar({ route, setRoute, collapsed, setCollapsed, theme }: {
  route: Route; setRoute: (r: Route) => void;
  collapsed: boolean; setCollapsed: (b: boolean) => void; theme: ThemeController;
}) {
  const items: { to: Route; label: string; icon: React.ReactNode }[] = [
    { to: '', label: 'Обзор', icon: <NavIcon d="M3 12l9-9 9 9M5 10v10a1 1 0 001 1h3m10-11v10a1 1 0 01-1 1h-3m-4 0h4" /> },
    { to: 'accounts', label: 'Счета', icon: <NavIcon d="M3 7h18M3 12h18M3 17h18M7 3v18" /> },
    { to: 'journal', label: 'Журнал', icon: <NavIcon d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-6 9h6m-6 4h6" /> },
    { to: 'goals', label: 'Цели', icon: <NavIcon d="M12 2v4m0 12v4M4.93 4.93l2.83 2.83m8.48 8.48l2.83 2.83M2 12h4m12 0h4M4.93 19.07l2.83-2.83m8.48-8.48l2.83-2.83M15 12a3 3 0 11-6 0 3 3 0 016 0z" /> },
    { to: 'settings', label: 'Настройки', icon: <NavIcon d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065zM15 12a3 3 0 11-6 0 3 3 0 016 0z" /> },
  ];

  return (
    <aside className={`sidebar ${collapsed ? 'collapsed' : ''}`}>
      <div className="sidebar-brand">
        <div className="brand-mark">СК</div>
        {!collapsed && <span>СчитайКа</span>}
      </div>
      <nav className="sidebar-nav">
        {items.map(i => (
          <button
            key={i.to}
            type="button"
            className={`nav-item ${route === i.to ? 'active' : ''}`}
            onClick={() => { setRoute(i.to); if (collapsed) setCollapsed(false); }}
          >
            {i.icon}
            {!collapsed && <span>{i.label}</span>}
          </button>
        ))}
      </nav>
      <div className="sidebar-footer">
        <button
          type="button" className="btn icon"
          title={theme.resolved === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему'}
          onClick={theme.toggle} aria-label={theme.resolved === 'dark' ? 'Светлая тема' : 'Тёмная тема'}
        >
          <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="1.8">
            {theme.resolved === 'dark'
              ? <path d="M12 3v2m0 14v2m9-9h-2M5 12H3m15.364-6.364l-1.414 1.414M7.05 16.95l-1.414 1.414M16.95 16.95l1.414 1.414M7.05 7.05L5.636 5.636M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
              : <path d="M21 12.79A9 9 0 1111.21 3a7 7 0 009.79 9.79z" />}
          </svg>
        </button>
        <button
          type="button" className="btn icon"
          title={collapsed ? 'Развернуть навигацию' : 'Свернуть навигацию'}
          onClick={() => setCollapsed(!collapsed)}
          aria-label={collapsed ? 'Развернуть навигацию' : 'Свернуть навигацию'}
        >
          <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="1.8">
            {collapsed ? <path d="M9 18l6-6-6-6" /> : <path d="M15 18l-6-6 6-6" />}
          </svg>
        </button>
      </div>
    </aside>
  );
}

// ---------- Верхняя панель ----------
function TopBar() {
  return (
    <header className="titlebar">
      <div className="titlebar-left">
        <div className="app-icon">СК</div>
        <span>СчитайКа</span>
      </div>
      <div className="titlebar-right">
        <button type="button" className="btn icon" title="Свернуть окно" aria-label="Свернуть">
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2"><path d="M5 12h14" /></svg>
        </button>
        <button type="button" className="btn icon" title="Развернуть окно" aria-label="Развернуть">
          <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" strokeWidth="2"><rect x="4" y="4" width="16" height="16" rx="1" /></svg>
        </button>
        <button type="button" className="btn icon danger" title="Закрыть окно" onClick={() => window.close?.()} aria-label="Закрыть">
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2"><path d="M6 6l12 12M18 6L6 18" /></svg>
        </button>
      </div>
    </header>
  );
}

export default function App() {
  const auth = useAuth();
  const theme = useTheme();
  const [route, setRoute] = useHashRoute();
  const [showQuick, setShowQuick] = useState(false);
  const [cats, setCats] = useState<Category[]>([]);
  const [accounts, setAccounts] = useState<AccountInfo[]>([]);
  const [tick, setTick] = useState(0);
  const [collapsed, setCollapsed] = useState(false);

  useEffect(() => {
    if (auth.user) {
      api.get<Category[]>('/api/categories').then(setCats).catch(() => {});
      api.get<AccountInfo[]>('/api/accounts').then(setAccounts).catch(() => {});
    }
  }, [auth.user, tick]);

  useEffect(() => {
    if (auth.loading) return;
    if (!auth.user) {
      if (route !== 'login') setRoute('login');
    } else if (route === 'login') {
      setRoute('');
    }
  }, [auth.loading, auth.user, route]);

  if (auth.loading)
    return <div className="app"><div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '100vh', width: '100%' }} className="card muted">Проверка сессии…</div></div>;
  if (!auth.user) return <LoginPage auth={auth} />;

  return (
    <ToastProvider>
      <div className="app">
        <Sidebar route={route} setRoute={setRoute} collapsed={collapsed} setCollapsed={setCollapsed} theme={theme} />
        <div className="shell">
          <TopBar />
          <main className="workarea">
            <div className="container fade-in">
              {route === 'journal' ? <Journal key={tick} />
                : route === 'accounts' ? <AccountsPage key={tick} onChanged={() => setTick(t => t + 1)} />
                : route === 'goals' ? <GoalsPage key={tick} />
                : route === 'settings' ? (
                  <>
                    <div style={{ maxWidth: 740 }}>
                      <Settings auth={auth} categories={cats} theme={theme} onReload={() => setTick(t => t + 1)} />
                    </div>
                    <div style={{ maxWidth: 740, marginTop: 16 }}>
                      <AccountRulesCard />
                    </div>
                  </>
                )
                : <Dashboard key={tick} />}
            </div>
          </main>
        </div>

        {route !== 'settings' && (
          <button className="fab" type="button" title="Добавить операцию" aria-label="Добавить операцию" onClick={() => setShowQuick(true)}>
            <svg viewBox="0 0 24 24" width="28" height="28" fill="none" stroke="currentColor" strokeWidth="2.2" strokeLinecap="round" aria-hidden="true">
              <path d="M12 5v14M5 12h14" />
            </svg>
          </button>
        )}
        {showQuick && (
          <QuickTxModal categories={cats} accounts={accounts}
            onClose={() => setShowQuick(false)} onSaved={() => setTick(t => t + 1)} />
        )}
      </div>
    </ToastProvider>
  );
}

// ============================================================================
// Настройки
// ============================================================================
function Settings({ auth, categories, theme, onReload }: {
  auth: ReturnType<typeof useAuth>; categories: Category[]; theme: ThemeController; onReload: () => void;
}) {
  return (
    <>
<<<<<<< HEAD
      <div className="card">
        <h3>Аккаунт</h3>
        <table>
          <tbody>
            <tr><td><b>Имя</b></td><td>{auth.user?.name}</td></tr>
            <tr><td><b>Email</b></td><td>{auth.user?.email}</td></tr>
          </tbody>
        </table>
        <StartingBalance auth={auth} />
        <div className="action-row">
          <button className="btn secondary" onClick={auth.logout}>Выйти</button>
        </div>
      </div>
      <AppearanceCard theme={theme} />
      <CategoriesCard categories={categories} onChanged={() => { onReload(); auth.refresh(); }} />
=======
      {/* Порядок разделов настроек: 1) Профиль, 2) Семья, 3) Категории */}
      <ProfileCard auth={auth} />
>>>>>>> bf4507991471ab9d18e3eda494a9d6cab04eb3b0
      <FamilyCard />
      <CategoriesCard categories={categories} onChanged={() => { onReload(); auth.refresh(); }} />
    </>
  );
}

<<<<<<< HEAD
function AppearanceCard({ theme }: { theme: ThemeController }) {
  const options: { value: ThemeMode; icon: React.ReactNode; label: string }[] = [
    { value: 'light', icon: <NavIcon d="M12 3v2m0 14v2m9-9h-2M5 12H3m15.364-6.364l-1.414 1.414M7.05 16.95l-1.414 1.414M16.95 16.95l1.414 1.414M7.05 7.05L5.636 5.636M15 12a3 3 0 11-6 0 3 3 0 016 0z" />, label: 'Светлая' },
    { value: 'dark', icon: <NavIcon d="M21 12.79A9 9 0 1111.21 3a7 7 0 009.79 9.79z" />, label: 'Тёмная' },
    { value: 'system', icon: <NavIcon d="M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z" />, label: 'Системная' },
  ];
  return (
    <div className="card">
      <h3>Оформление</h3>
      <div className="muted">Тема применяется сразу и запоминается на этом устройстве.</div>
      <div className="segmented" style={{ display: 'flex', gap: 8, marginTop: 12, flexWrap: 'wrap' }}>
        {options.map(o => (
          <button
            key={o.value}
            type="button"
            className={`btn ${theme.mode === o.value ? 'primary' : 'secondary'}`}
            onClick={() => theme.setMode(o.value)}
            style={{ display: 'inline-flex', alignItems: 'center', gap: 8 }}
          >
            {o.icon} {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}

function StartingBalance({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [val, setVal] = useState(String(auth.user?.startingBalance ?? 0));
=======
// Профиль: имя, email, стартовый баланс, выход
function ProfileCard({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [name, setName] = useState(auth.user?.name ?? '');
  const [bal, setBal] = useState(String(auth.user?.startingBalance ?? 0));
>>>>>>> bf4507991471ab9d18e3eda494a9d6cab04eb3b0
  const [msg, setMsg] = useState('');
  const [err, setErr] = useState('');
  const save = async (e: React.FormEvent) => {
    e.preventDefault(); setMsg(''); setErr('');
    try {
<<<<<<< HEAD
      await api.post('/api/budget/start-balance', { startingBalance: Number(val.replace(',', '.')) || 0 });
      setMsg('Сохранено'); auth.refresh();
    } catch (ex: any) { setMsg(ex.message); }
  };
  return (
    <form onSubmit={save} style={{ marginTop: 14 }}>
      <label>Стартовый баланс на счетах, ₽
        <input inputMode="decimal" value={val} onChange={e => setVal(e.target.value)} placeholder="0" />
      </label>
      {msg && <div className="muted" style={{ marginBottom: 8 }}>{msg}</div>}
      <button className="btn primary small" type="submit">Сохранить</button>
    </form>
=======
      await api.post('/api/family/profile', { name: name.trim(), startingBalance: Number(bal.replace(',', '.')) || 0 });
      setMsg('Сохранено ✓'); auth.refresh();
    } catch (ex: any) { setErr(ex.message); }
  };
  return (
    <div className="card">
      <h3>👤 Профиль</h3>
      <form onSubmit={save}>
        <div className="form-row">
          <label>Имя<input value={name} onChange={e => setName(e.target.value)} required placeholder="Как вас зовут?" /></label>
          <label>Стартовый баланс на счетах, ₽
            <input inputMode="decimal" value={bal} onChange={e => setBal(e.target.value)} placeholder="0" />
          </label>
        </div>
        <div className="muted" style={{ margin: '4px 0' }}>Email: {auth.user?.email}</div>
        {err && <div className="alert">{err}</div>}
        {msg && <div className="muted">{msg}</div>}
        <div className="form-row" style={{ gap: 8 }}>
          <button className="btn primary small">Сохранить профиль</button>
          <button type="button" className="btn danger-ghost small" onClick={auth.logout}>Выйти</button>
        </div>
      </form>
    </div>
>>>>>>> bf4507991471ab9d18e3eda494a9d6cab04eb3b0
  );
}

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
      <h3>Категории</h3>
      <table>
        <thead><tr><th>Категория</th><th>Тип</th><th></th></tr></thead>
        <tbody>
          {categories.map(c => (
            <tr key={c.id}>
              <td><span style={{ color: c.color, fontWeight: 700, marginRight: 6 }}>●</span>{c.name}</td>
              <td>{c.type === 0 ? 'доход' : 'расход'}</td>
              <td><button type="button" className="btn icon danger small" onClick={() => del(c.id)} title="Удалить">✕</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <form onSubmit={add} style={{ marginTop: 16 }}>
        <div className="form-row">
          <label>Название<input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Бензин" /></label>
          <label>Тип
            <select value={type} onChange={e => setType(Number(e.target.value) as 0 | 1)}>
              <option value={1}>Расход</option><option value={0}>Доход</option>
            </select>
          </label>
          <label>Цвет<input type="color" value={color} onChange={e => setColor(e.target.value)} style={{ height: 40, padding: 2 }} /></label>
        </div>
        {err && <div className="alert alert-danger">{err}</div>}
        <button className="btn primary" type="submit">+ Добавить категорию</button>
      </form>
    </div>
  );
}
