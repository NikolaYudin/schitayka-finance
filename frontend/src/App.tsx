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

// ---------- Р‘РѕРєРѕРІР°СЏ РїР°РЅРµР»СЊ РЅР°РІРёРіР°С†РёРё (Sidebar, 280px / 48px СЃРІС‘СЂРЅСѓС‚Р°СЏ) ----------
function NavIcon({ path }: { path: string }) {
  return (
    <svg viewBox="0 0 24 24" width="20" height="20" fill="currentColor" aria-hidden="true">
      <path d={path} fill="currentColor" />
    </svg>
  );
}

function Sidebar({ route, setRoute, collapsed, setCollapsed, theme }: {
  route: Route; setRoute: (r: Route) => void;
  collapsed: boolean; setCollapsed: (b: boolean) => void; theme: ThemeController;
}) {
  const items: { to: Route; label: string; icon: React.ReactNode }[] = [
    { to: '', label: 'РћР±Р·РѕСЂ', icon: <NavIcon path="M3 3v18h18V3H3zm16 16H5V5h14z" /> },
    { to: 'accounts', label: 'РЎС‡РµС‚Р°', icon: <NavIcon path="M1 11h5V5H1zm0 6h5v-5H1zm11 0h5v-5h-5zm-6 0h5v-5H6z" /> },
    { to: 'journal', label: 'Р–СѓСЂРЅР°Р»', icon: <NavIcon path="M14 2H6c-1.1 0-1.99.9-1.99 2L4 16c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2zm-2 10l-2.5-5-2.5 5H6v-4h4zm2-11L11 3l-2.5 5-2.5-5H2l4 8 4-8z" /> },
    { to: 'goals', label: 'Р¦РµР»Рё', icon: <NavIcon path="M12 2L3 7v13c0 1.88 1.34 3.45 3 3.81V17h6v-2.19c1.66-.36 3-1.93 3-3.81V7zm-1 9L5.5 5 11 11l5.5-9z" /> },
    { to: 'settings', label: 'РќР°СЃС‚СЂРѕР№РєРё', icon: <NavIcon path="M19.43 12.98c.04-.32.07-.64.07-.98s-.03-.66-.07-.98l2.11-1.65c.19-.15.24-.41.14-.65l-1.04-3.57c-.16-.55-.73-.89-1.28-.73l-2.31.48c-.47-.34-1.03-.51-1.59-.46l-2.17-1.7c-.35-.27-.82-.25-1.15.07l-1.47 1.44c-.38.37-.54.95-.38 1.46l.64 2.27c-.81.14-1.58.02-2.24-.34l-2.51 1.2c-.39.19-.57.62-.46 1.05l.36 1.4c-.75.27-1.43.46-2.12.44v1.82c0 .68-.28 1.32-.75 1.75l2.15 2.15c.45.42 1.05.69 1.69.69s1.24-.27 1.69-.69l2.15-2.15c.47-.43.75-1.07.75-1.75h-1.82c.69.06 1.34-.19 1.76-.63l1.4-1.4c.36-.36.83-.36 1.19 0l1.09 1.09c.49-.58.77-1.3.74-2.06l1.64-1.58c.31-.36.37-.91.18-1.37z" /> },
  ];

  return (
    <aside className={`sidebar ${collapsed ? 'collapsed' : ''}`}>
      <div className="sidebar-brand">
        <div className="brand-mark">РЎРљ</div>
        {!collapsed && <span>РЎС‡РёС‚Р°Р№РљР°</span>}
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
            {!collapsed && i.label}
          </button>
        ))}
      </nav>
      <div className="sidebar-footer">
        <button
          type="button" className="btn icon"
          title={theme.resolved === 'dark' ? 'Р’РєР»СЋС‡РёС‚СЊ СЃРІРµС‚Р»СѓСЋ С‚РµРјСѓ' : 'Р’РєР»СЋС‡РёС‚СЊ С‚С‘РјРЅСѓСЋ С‚РµРјСѓ'}
          onClick={theme.toggle} aria-label={theme.resolved === 'dark' ? 'РЎРІРµС‚Р»Р°СЏ С‚РµРјР°' : 'РўС‘РјРЅР°СЏ С‚РµРјР°'}
        >
          {theme.resolved === 'dark' ? 'вЂ' : 'вѕ'}
        </button>
        <button
          type="button" className="btn icon"
          title={collapsed ? 'Р Р°Р·РІРµСЂРЅСѓС‚СЊ РЅР°РІРёРіР°С†РёСЋ' : 'РЎРІРµСЂРЅСѓС‚СЊ РЅР°РІРёРіР°С†РёСЋ'}
          onClick={() => setCollapsed(!collapsed)}
          aria-label={collapsed ? 'Р Р°Р·РІРµСЂРЅСѓС‚СЊ РЅР°РІРёРіР°С†РёСЋ' : 'РЎРІРµСЂРЅСѓС‚СЊ РЅР°РІРёРіР°С†РёСЋ'}
        >
          {collapsed ? 'вЂє' : 'вЂ№'}
        </button>
      </div>
    </aside>
  );
}

// ---------- Р’РµСЂС…РЅСЏСЏ РїР°РЅРµР»СЊ (Title Bar, 40px) ----------
function TopBar() {
  return (
    <header className="titlebar">
      <div className="titlebar-left">
        <div className="app-icon">РЎРљ</div>
        <span>РЎС‡РёС‚Р°Р№РљР°</span>
      </div>
      <div className="titlebar-right">
        <button type="button" className="btn icon" title="РЎРІРµСЂРЅСѓС‚СЊ РѕРєРЅРѕ">вЂ”</button>
        <button type="button" className="btn icon" title="Р Р°Р·РІРµСЂРЅСѓС‚СЊ РѕРєРЅРѕ">в–Ў</button>
        <button type="button" className="btn icon danger" title="Р—Р°РєСЂС‹С‚СЊ РѕРєРЅРѕ" onClick={() => window.close?.()} aria-label="Р—Р°РєСЂС‹С‚СЊ">вњ•</button>
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
  const [tick, setTick] = useState(0);   // РїРµСЂРµР·Р°РіСЂСѓР·РєР° СЃС‚СЂР°РЅРёС† РїРѕСЃР»Рµ СЃРѕС…СЂР°РЅРµРЅРёСЏ
  const [collapsed, setCollapsed] = useState(false); // СЃРѕСЃС‚РѕСЏРЅРёРµ СЃР°Р№РґР±Р°СЂР°

  // РљР°С‚РµРіРѕСЂРёРё Рё СЃС‡РµС‚Р° РґР»СЏ Р±С‹СЃС‚СЂРѕР№ РѕРїРµСЂР°С†РёРё
  useEffect(() => {
    if (auth.user) {
      api.get<Category[]>('/api/categories').then(setCats).catch(() => {});
      api.get<AccountInfo[]>('/api/accounts').then(setAccounts).catch(() => {});
    }
  }, [auth.user, tick]);

  // РќР°РІРёРіР°С†РёСЏ вЂ” С‚РѕР»СЊРєРѕ РІ СЌС„С„РµРєС‚Рµ
  useEffect(() => {
    if (auth.loading) return;
    if (!auth.user) {
      if (route !== 'login') setRoute('login');
    } else if (route === 'login') {
      setRoute('');
    }
  }, [auth.loading, auth.user, route]);

  if (auth.loading)
    return <div className="app"><div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '100vh' }} className="card muted">РџСЂРѕРІРµСЂРєР° СЃРµСЃСЃРёРёвЂ¦</div></div>;
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

        {/* FAB РґРѕСЃС‚СѓРїРЅР° РЅР° РѕСЃРЅРѕРІРЅС‹С… СЌРєСЂР°РЅР°С… */}
        {route !== 'settings' && (
          <button className="fab" title="Р”РѕР±Р°РІРёС‚СЊ РѕРїРµСЂР°С†РёСЋ" onClick={() => setShowQuick(true)}>+</button>
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
// РќР°СЃС‚СЂРѕР№РєРё (Settings) вЂ” РµРґРёРЅР°СЏ СЃС‚СЂР°РЅРёС†Р° РїСЂРѕС„РёР»СЏ, РєР°С‚РµРіРѕСЂРёРё, СЃРµРјСЊСЏ, С‚РµРјР°, РїСЂР°РІРёР»Р°
// ============================================================================
function Settings({ auth, categories, theme, onReload }: {
  auth: ReturnType<typeof useAuth>; categories: Category[]; theme: ThemeController; onReload: () => void;
}) {
  return (
    <>
      <div className="card">
        <h3>РђРєРєР°СѓРЅС‚</h3>
        <table>
          <tbody>
            <tr><td><b>РРјСЏ</b></td><td>{auth.user?.name}</td></tr>
            <tr><td><b>Email</b></td><td>{auth.user?.email}</td></tr>
          </tbody>
        </table>
        <StartingBalance auth={auth} />
        <div className="action-row">
          <button className="btn danger-ghost" onClick={auth.logout}>Р’С‹Р№С‚Рё</button>
        </div>
      </div>
      <AppearanceCard theme={theme} />
      <CategoriesCard categories={categories} onChanged={() => { onReload(); auth.refresh(); }} />
      <FamilyCard />
    </>
  );
}

// РћС„РѕСЂРјР»РµРЅРёРµ: СЃРІРµС‚Р»Р°СЏ / С‚С‘РјРЅР°СЏ / СЃРёСЃС‚РµРјРЅР°СЏ С‚РµРјР°
function AppearanceCard({ theme }: { theme: ThemeController }) {
  const options: { value: ThemeMode; icon: string; label: string }[] = [
    { value: 'light', icon: 'вЂ', label: 'РЎРІРµС‚Р»Р°СЏ' },
    { value: 'dark', icon: 'вѕ', label: 'РўС‘РјРЅР°СЏ' },
    { value: 'system', icon: 'вЊ‚', label: 'РЎРёСЃС‚РµРјРЅР°СЏ' },
  ];
  return (
    <div className="card">
      <h3>РћС„РѕСЂРјР»РµРЅРёРµ</h3>
      <div className="muted">РўРµРјР° РїСЂРёРјРµРЅСЏРµС‚СЃСЏ СЃСЂР°Р·Сѓ Рё Р·Р°РїРѕРјРёРЅР°РµС‚СЃСЏ РЅР° СЌС‚РѕРј СѓСЃС‚СЂРѕР№СЃС‚РІРµ.</div>
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

// РЎС‚Р°СЂС‚РѕРІС‹Р№ Р±Р°Р»Р°РЅСЃ вЂ” В«СЃРєРѕР»СЊРєРѕ РґРµРЅРµРі РЅР° СЃС‡РµС‚Р°С… СЃРµР№С‡Р°СЃВ», РІР»РёСЏРµС‚ РЅР° РёС‚РѕРіРѕРІС‹Р№ Р±Р°Р»Р°РЅСЃ РІ РћР±Р·РѕСЂРµ
function StartingBalance({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [val, setVal] = useState(String(auth.user?.startingBalance ?? 0));
  const [msg, setMsg] = useState('');
  const save = async (e: React.FormEvent) => {
    e.preventDefault(); setMsg('');
    try {
      await api.post('/api/budget/start-balance', { startingBalance: Number(val.replace(',', '.')) || 0 });
      setMsg('РЎРѕС…СЂР°РЅРµРЅРѕ вњ“'); auth.refresh();
    } catch (ex: any) { setMsg(ex.message); }
  };
  return (
    <form onSubmit={save} style={{ marginTop: 10 }}>
      <label>РЎС‚Р°СЂС‚РѕРІС‹Р№ Р±Р°Р»Р°РЅСЃ РЅР° СЃС‡РµС‚Р°С…, в‚Ѕ
        <input inputMode="decimal" value={val} onChange={e => setVal(e.target.value)} placeholder="0" />
      </label>
      {msg && <div className="muted">{msg}</div>}
      <button className="btn primary small">РЎРѕС…СЂР°РЅРёС‚СЊ</button>
    </form>
  );
}

// РЈРїСЂР°РІР»РµРЅРёРµ РєР°С‚РµРіРѕСЂРёСЏРјРё (Р»РёС‡РЅС‹Рµ СЃРїСЂР°РІРѕС‡РЅРёРєРё)
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
    if (!confirm('РЈРґР°Р»РёС‚СЊ РєР°С‚РµРіРѕСЂРёСЋ?')) return;
    try { await api.del(`/api/categories/${id}`); onChanged(); }
    catch (ex: any) { alert(ex.message); }
  };

  return (
    <div className="card">
      <h3>РљР°С‚РµРіРѕСЂРёРё</h3>
      <table>
        <thead><tr><th>РљР°С‚РµРіРѕСЂРёСЏ</th><th>РўРёРї</th><th></th></tr></thead>
        <tbody>
          {categories.map(c => (
            <tr key={c.id}>
              <td><b style={{ color: c.color }}>в—Џ</b> {c.name}</td>
              <td>{c.type === 0 ? 'РґРѕС…РѕРґ' : 'СЂР°СЃС…РѕРґ'}</td>
              <td><button className="btn danger-ghost small" onClick={() => del(c.id)}>вњ•</button></td>
            </tr>
          ))}
        </tbody>
      </table>
      <form onSubmit={add} style={{ marginTop: 12 }}>
        <div className="form-row">
          <label>РќР°Р·РІР°РЅРёРµ<input value={name} onChange={e => setName(e.target.value)} required placeholder="РќР°РїСЂ.: Р‘РµРЅР·РёРЅ" /></label>
          <label>РўРёРї
            <select value={type} onChange={e => setType(Number(e.target.value) as 0 | 1)}>
              <option value={1}>Р Р°СЃС…РѕРґ</option><option value={0}>Р”РѕС…РѕРґ</option>
            </select>
          </label>
          <label>Р¦РІРµС‚<input type="color" value={color} onChange={e => setColor(e.target.value)} style={{ height: 40, padding: 2 }} /></label>
        </div>
        {err && <div className="alert">{err}</div>}
        <button className="btn primary">+ Р”РѕР±Р°РІРёС‚СЊ РєР°С‚РµРіРѕСЂРёСЋ</button>
      </form>
    </div>
  );
}

