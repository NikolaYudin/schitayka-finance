import { useEffect, useState } from 'react';
import { useAuth } from './auth';
import { api, Category, AccountInfo, UpdateCheck } from './api';
import LoginPage from './LoginPage';
import { Dashboard, Journal, QuickTxModal } from './components';
import { GoalsPage } from './GoalsPage';
import { AccountsPage, FamilyCard } from './AccountsPage';
import { InvestmentsPage } from './InvestmentsPage';
import { AccountRulesCard } from './AccountRulesCard';
import { useTheme, type ThemeController, type ThemeMode } from './theme';
import { ToastProvider } from './toasts';
import { Icon, type IconName } from './icons';
import { plural } from './plural';

type Route = '' | 'journal' | 'budget' | 'goals' | 'accounts' | 'investments' | 'settings' | 'login';

const ROUTES: Route[] = ['', 'journal', 'budget', 'goals', 'accounts', 'investments', 'settings'];

function useHashRoute(): [Route, (r: Route) => void] {
  const parse = (): Route => {
    const h = location.hash.replace(/^#\/?/, '').split('?')[0];
    return (ROUTES as string[]).includes(h) ? (h as Route) : '';
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

// ---------- Навигация ----------
interface NavItem { to: Route; label: string; icon: IconName; }

const NAV: NavItem[] = [
  { to: '', label: 'Дашборд', icon: 'dashboard' },
  { to: 'journal', label: 'Операции', icon: 'operations' },
  { to: 'budget', label: 'Бюджет и лимиты', icon: 'budget' },
  { to: 'goals', label: 'Цели', icon: 'target' },
  { to: 'accounts', label: 'Счета', icon: 'wallet' },
  { to: 'investments', label: 'Инвестиции', icon: 'trending-up' },
  { to: 'settings', label: 'Настройки', icon: 'settings' },
];

const PAGE_SUBTITLE: Record<Route, string> = {
  '': '',
  journal: 'История доходов и расходов',
  budget: 'Месячные лимиты и контроль трат',
  goals: 'Копилки, сроки и ежемесячные отчисления',
  accounts: 'Карты, наличные и вклады',
  investments: 'Портфели, доходность и сравнение с копилкой',
  settings: 'Профиль, оформление и правила',
  login: 'Вход в приложение',
};

function today(): string {
  const s = new Intl.DateTimeFormat('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' }).format(new Date());
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function initials(name?: string): string {
  if (!name) return 'Я';
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return 'Я';
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

// ---------- Боковая панель ----------
function Sidebar({ route, setRoute, collapsed, setCollapsed, theme, mobileOpen, closeMobile, hasUpdate }: {
  route: Route; setRoute: (r: Route) => void;
  collapsed: boolean; setCollapsed: (b: boolean) => void;
  theme: ThemeController; mobileOpen: boolean; closeMobile: () => void; hasUpdate: boolean;
}) {
  return (
    <>
      <aside className={`sidebar ${collapsed ? 'is-collapsed' : ''} ${mobileOpen ? 'is-open' : ''}`}>
        <div className="brand">
          <div className="brand-mark"><Icon name="calculator" size={22} /></div>
          <div className="brand-text">
            <b>СчитайКа</b>
            <span>Личные финансы</span>
          </div>
          <button type="button" className="sidebar-close" onClick={closeMobile} aria-label="Закрыть меню">
            <Icon name="x" size={17} />
          </button>
        </div>

        <nav className="nav" aria-label="Основная навигация">
          <div className="nav-section">Меню</div>
          {NAV.map(i => (
            <button
              key={i.to}
              type="button"
              className={`nav-item ${route === i.to ? 'active' : ''}`}
              onClick={() => { setRoute(i.to); closeMobile(); }}
              title={collapsed ? i.label : undefined}
              aria-current={route === i.to ? 'page' : undefined}
            >
              <span className="nav-ico"><Icon name={i.icon} size={19} /></span>
              <span className="nav-label">{i.label}</span>
              {i.to === 'settings' && hasUpdate && (
                <span className="nav-dot" title="Доступно обновление" aria-label="Доступно обновление" />
              )}
            </button>
          ))}
        </nav>

        <div className="sidebar-foot">
          <div className="theme-mini" role="group" aria-label="Тема оформления">
            {([
              ['light', 'sun', 'Светлая'],
              ['dark', 'moon', 'Тёмная'],
              ['system', 'monitor', 'Системная'],
            ] as const).map(([mode, icon, label]) => (
              <button
                key={mode}
                type="button"
                className={`theme-mini-btn ${theme.mode === mode ? 'active' : ''}`}
                onClick={() => theme.setMode(mode as ThemeMode)}
                title={label}
                aria-label={label}
              >
                <Icon name={icon} size={15} />
              </button>
            ))}
          </div>
          <button
            type="button"
            className="collapse-btn"
            title={collapsed ? 'Развернуть навигацию' : 'Свернуть навигацию'}
            aria-label={collapsed ? 'Развернуть навигацию' : 'Свернуть навигацию'}
            onClick={() => setCollapsed(!collapsed)}
          >
            <Icon name={collapsed ? 'chevron-right' : 'chevron-left'} size={16} />
          </button>
        </div>
      </aside>
      <button
        type="button"
        className={`sidebar-scrim ${mobileOpen ? 'is-open' : ''}`}
        onClick={closeMobile}
        aria-label="Закрыть меню"
        tabIndex={mobileOpen ? 0 : -1}
      />
    </>
  );
}

// ---------- Верхняя панель ----------
function TopBar({ route, theme, onAdd, onMenu, user, hasUpdate }: {
  route: Route; theme: ThemeController; onAdd: () => void; onMenu: () => void;
  user: { name?: string } | null; hasUpdate: boolean;
}) {
  const title = route === 'budget' ? 'Бюджет и лимиты'
    : route === 'journal' ? 'Операции'
      : route === 'goals' ? 'Цели'
        : route === 'accounts' ? 'Счета'
          : route === 'investments' ? 'Инвестиции'
            : route === 'settings' ? 'Настройки'
              : 'Дашборд';
  const subtitle = route === '' ? today() : PAGE_SUBTITLE[route];

  return (
    <header className="topbar">
      <div className="topbar-left">
        <button type="button" className="btn icon menu-btn" onClick={onMenu} aria-label="Открыть меню">
          <Icon name="menu" size={19} />
        </button>
        <div>
          <div className="topbar-title">{title}</div>
          <div className="topbar-sub">{subtitle}</div>
        </div>
      </div>

      <div className="topbar-right">
        {hasUpdate && (
          <a className="update-pill" href="#/settings" title="Доступно обновление приложения">
            <Icon name="sparkles" size={14} /> Обновление
          </a>
        )}
        <div className="theme-switch" role="group" aria-label="Тема оформления">
          {([
            ['light', 'sun', 'Светлая тема'],
            ['dark', 'moon', 'Тёмная тема'],
            ['system', 'monitor', 'Системная тема'],
          ] as const).map(([mode, icon, label]) => (
            <button
              key={mode}
              type="button"
              className={`theme-btn ${theme.mode === mode ? 'active' : ''}`}
              onClick={() => theme.setMode(mode as ThemeMode)}
              title={label}
              aria-label={label}
            >
              <Icon name={icon} size={16} />
            </button>
          ))}
        </div>
        <button type="button" className="btn primary add-btn" onClick={onAdd}>
          <Icon name="plus" size={17} strokeWidth={2.3} />
          <span className="add-btn-text">Операция</span>
        </button>
        <div className="avatar" title={user?.name ?? 'Профиль'} aria-hidden="true">
          {initials(user?.name)}
        </div>
      </div>
    </header>
  );
}

// ---------- Приложение ----------
export default function App() {
  const auth = useAuth();
  const theme = useTheme();
  const [route, setRoute] = useHashRoute();
  const [showQuick, setShowQuick] = useState(false);
  const [cats, setCats] = useState<Category[]>([]);
  const [accounts, setAccounts] = useState<AccountInfo[]>([]);
  const [tick, setTick] = useState(0);
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    try { return localStorage.getItem('sk_sidebar_collapsed') === '1'; } catch { return false; }
  });
  const [mobileOpen, setMobileOpen] = useState(false);
  const [update, setUpdate] = useState<UpdateCheck | null>(null);
  const [updateBusy, setUpdateBusy] = useState(false);
  const [updateError, setUpdateError] = useState('');

  useEffect(() => {
    try { localStorage.setItem('sk_sidebar_collapsed', collapsed ? '1' : '0'); } catch { /* ignore */ }
  }, [collapsed]);

  useEffect(() => {
    if (auth.user) {
      api.get<Category[]>('/api/categories').then(setCats).catch(() => {});
      api.get<AccountInfo[]>('/api/accounts').then(setAccounts).catch(() => {});
    }
  }, [auth.user, tick]);

  // Слежение за версией: один автоматический запрос при входе, дальше — по кнопке.
  useEffect(() => {
    if (!auth.user) { setUpdate(null); return; }
    let cancelled = false;
    setUpdateBusy(true);
    api.get<UpdateCheck>('/api/version/check')
      .then(r => { if (!cancelled) { setUpdate(r); setUpdateError(''); } })
      .catch(e => { if (!cancelled) setUpdateError(e.message || 'Не удалось проверить обновления'); })
      .finally(() => { if (!cancelled) setUpdateBusy(false); });
    return () => { cancelled = true; };
  }, [auth.user]);

  const checkUpdates = () => {
    setUpdateBusy(true); setUpdateError('');
    api.get<UpdateCheck>('/api/version/check')
      .then(setUpdate)
      .catch(e => setUpdateError(e.message || 'Не удалось проверить обновления'))
      .finally(() => setUpdateBusy(false));
  };

  // «Слежка» в фоне: раз в 6 часов, пока приложение открыто.
  useEffect(() => {
    if (!auth.user) return;
    const timer = window.setInterval(() => {
      api.get<UpdateCheck>('/api/version/check')
        .then(r => { setUpdate(r); setUpdateError(''); })
        .catch(() => { /* тихо: не мешаем работе */ });
    }, 6 * 60 * 60 * 1000);
    return () => window.clearInterval(timer);
  }, [auth.user]);

  useEffect(() => {
    if (auth.loading) return;
    if (!auth.user) {
      if (route !== 'login') setRoute('login');
    } else if (route === 'login') {
      setRoute('');
    }
  }, [auth.loading, auth.user, route]);

  if (auth.loading) {
    return (
      <div className="splash">
        <div className="splash-mark"><Icon name="calculator" size={28} /></div>
        <span>Проверяем сессию…</span>
      </div>
    );
  }
  if (!auth.user) return <LoginPage auth={auth} />;

  const page = route === 'journal' ? <Journal key={tick} />
    : route === 'accounts' ? <AccountsPage key={tick} onChanged={() => setTick(t => t + 1)} />
    : route === 'investments' ? <InvestmentsPage key={tick} />
      : route === 'goals' ? <GoalsPage key={route} initialTab="goals" />
        : route === 'budget' ? <GoalsPage key={route} initialTab="limits" />
          : route === 'settings' ? (
            <Settings
              auth={auth}
              categories={cats}
              theme={theme}
              onReload={() => setTick(t => t + 1)}
              update={update}
              updateBusy={updateBusy}
              updateError={updateError}
              onCheckUpdates={checkUpdates}
            />
          )
            : <Dashboard key={tick} onAdd={() => setShowQuick(true)} />;

  return (
    <ToastProvider>
      <div className={`app ${collapsed ? 'is-collapsed' : ''}`}>
        <Sidebar
          route={route}
          setRoute={setRoute}
          collapsed={collapsed}
          setCollapsed={setCollapsed}
          theme={theme}
          mobileOpen={mobileOpen}
          closeMobile={() => setMobileOpen(false)}
          hasUpdate={!!update?.updateAvailable}
        />
        <div className="shell">
          <TopBar
            route={route}
            theme={theme}
            user={auth.user}
            onAdd={() => setShowQuick(true)}
            onMenu={() => setMobileOpen(true)}
            hasUpdate={!!update?.updateAvailable}
          />
          <main className="page-scroll">
            <div className="page page-enter" key={route}>
              {page}
            </div>
          </main>
        </div>

        {route !== 'settings' && (
          <button
            type="button"
            className="fab"
            title="Добавить операцию"
            aria-label="Добавить операцию"
            onClick={() => setShowQuick(true)}
          >
            <Icon name="plus" size={24} strokeWidth={2.4} />
            <span className="fab-text">Добавить</span>
          </button>
        )}

        {showQuick && (
          <QuickTxModal
            categories={cats}
            accounts={accounts}
            onClose={() => setShowQuick(false)}
            onSaved={() => setTick(t => t + 1)}
          />
        )}
      </div>
    </ToastProvider>
  );
}

// ============================================================================
// Настройки
// ============================================================================
function Settings({ auth, categories, theme, onReload, update, updateBusy, updateError, onCheckUpdates }: {
  auth: ReturnType<typeof useAuth>; categories: Category[]; theme: ThemeController; onReload: () => void;
  update: UpdateCheck | null; updateBusy: boolean; updateError: string; onCheckUpdates: () => void;
}) {
  return (
    <>
      <UpdateCard
        update={update}
        busy={updateBusy}
        error={updateError}
        onCheck={onCheckUpdates}
      />
      <div className="settings-grid">
        <div className="settings-col">
          <ProfileCard auth={auth} />
          <FamilyCard />
        </div>
        <div className="settings-col">
          <AppearanceCard theme={theme} />
          <CategoriesCard categories={categories} onChanged={() => { onReload(); auth.refresh(); }} />
        </div>
      </div>
      <ExportCard />
      <AccountRulesCard />
    </>
  );
}

function ExportCard() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);

  const download = async () => {
    setBusy(true);
    setError('');
    setSaved(false);
    try {
      await api.download('/api/export/profile', 'schitayka-profile.json');
      setSaved(true);
      window.setTimeout(() => setSaved(false), 5000);
    } catch (e: any) {
      setError(e.message || 'Не удалось скачать JSON профиля.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="card export-card">
      <div className="card-head">
        <div>
          <h3>Данные для ИИ-анализа</h3>
          <p className="card-sub">Полный профиль одним JSON-файлом</p>
        </div>
        <span className="badge badge-muted">JSON</span>
      </div>

      <div className="export-points">
        <span><Icon name="check" size={14} /> счета и операции</span>
        <span><Icon name="check" size={14} /> категории и лимиты</span>
        <span><Icon name="check" size={14} /> цели и прогресс</span>
        <span><Icon name="check" size={14} /> правила и резервы</span>
        <span><Icon name="check" size={14} /> месячные агрегаты</span>
      </div>

      <p className="muted export-note">
        В файл попадают только ваши данные: без пароля, JWT, кода семьи и чужих семейных счетов.
        Передавайте JSON только доверенному ИИ-сервису — внутри ваша финансовая история.
      </p>

      {error && <div className="alert alert-warn">{error}</div>}
      {saved && <div className="alert alert-info">JSON профиля сохранён.</div>}

      <div className="action-row">
        <button className="btn primary" type="button" onClick={download} disabled={busy}>
          <Icon name="download" size={16} /> {busy ? 'Формируем…' : 'Скачать JSON профиля'}
        </button>
      </div>
    </div>
  );
}

function UpdateCard({ update, busy, error, onCheck }: {
  update: UpdateCheck | null; busy: boolean; error: string; onCheck: () => void;
}) {
  const [copied, setCopied] = useState(false);
  const current = update?.current;
  const latest = update?.latest;
  const repoUrl = update?.repoUrl ?? 'https://github.com/NikolaYudin/schitayka-finance';
  const updateCmd = 'cd /volume1/docker/schitayka && git pull --ff-only && sh deploy/xpenology/update-inplace.sh --no-pull';

  const when = (value?: string | null) => value
    ? new Date(value).toLocaleString('ru-RU', {
      day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit',
    })
    : '—';

  const copy = () => {
    const done = () => {
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2200);
    };
    const fallback = () => {
      try {
        const ta = document.createElement('textarea');
        ta.value = updateCmd;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.top = '-1000px';
        document.body.appendChild(ta);
        ta.select();
        const ok = document.execCommand('copy');
        ta.remove();
        if (ok) done();
      } catch { /* ignore */ }
    };

    try {
      if (!navigator.clipboard) { fallback(); return; }
      navigator.clipboard.writeText(updateCmd).then(done).catch(fallback);
    } catch {
      fallback();
    }
  };

  return (
    <div className="card update-card">
      <div className="card-head">
        <div>
          <h3>Версия и обновления</h3>
          <p className="card-sub">
            Слежение за <a href={repoUrl} target="_blank" rel="noreferrer">GitHub-репозиторием</a>
          </p>
        </div>
        <span className="badge badge-muted">v{current?.version ?? '—'}</span>
      </div>

      <div className="update-meta">
        <div><span>Текущая версия</span><b>{current?.version ?? '—'}</b></div>
        <div><span>Коммит</span><b>{current && current.commit !== 'unknown' ? current.shortCommit : '—'}</b></div>
        <div><span>Сборка</span><b>{when(current?.buildDate)}</b></div>
      </div>

      {busy && !update && <div className="alert alert-info">Проверяем обновления…</div>}

      {update?.ok && latest && (
        update.updateAvailable ? (
          <div className="update-banner">
            <span className="update-banner-ico"><Icon name="trending-up" size={21} /></span>
            <div>
              <b>Доступно обновление{latest.version ? ` · v${latest.version}` : ''}</b>
              <div className="muted">GitHub: {latest.shortSha} · {when(latest.date)}</div>
              {latest.message && <div className="update-message">{latest.message}</div>}
              <div style={{ marginTop: 4 }}>
                <a href={latest.url} target="_blank" rel="noreferrer">Открыть коммит на GitHub</a>
              </div>
            </div>
          </div>
        ) : (
          <div className="rec rec-good">
            <span className="rec-icon"><Icon name="check" size={16} /></span>
            <span>Установлена последняя версия из ветки GitHub.</span>
          </div>
        )
      )}

      {update && !update.ok && (
        <div className="alert alert-warn">Не удалось проверить обновления: {update.error}</div>
      )}
      {error && <div className="alert alert-warn">Не удалось проверить обновления: {error}</div>}

      {(update?.updateAvailable || (update && !update.ok)) && (
        <>
          <div className="update-cmd">
            <code>{updateCmd}</code>
            <button className="btn small" type="button" onClick={copy}>
              <Icon name={copied ? 'check' : 'copy'} size={14} /> {copied ? 'Скопировано' : 'Копировать'}
            </button>
          </div>
          <p className="muted update-note">
            Скрипт заменяет код внутри работающего контейнера и перезапускает его:
            контейнер <b>не пересоздаётся</b>, база в томе <b>schitayka-data</b> сохраняется.
            Если менялись <code>docker-compose.yml</code> или <code>.env</code> — используйте <code>update.sh</code>.
            Путь <code>/volume1/docker/schitayka</code> в команде замените на свой, если проект лежит в другом месте.
          </p>
        </>
      )}

      <div className="action-row">
        <button className="btn primary" type="button" onClick={onCheck} disabled={busy}>
          <Icon name="refresh" size={16} /> {busy ? 'Проверяем…' : 'Проверить обновления'}
        </button>
        <a className="btn secondary" href={repoUrl} target="_blank" rel="noreferrer">
          <Icon name="arrow-up-right" size={15} /> GitHub
        </a>
      </div>
      {update && (
        <div className="muted" style={{ marginTop: 8 }}>
          Проверено: {when(update.checkedAt)}
        </div>
      )}
    </div>
  );
}

function ProfileCard({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const [name, setName] = useState(auth.user?.name ?? '');
  const [bal, setBal] = useState(String(auth.user?.startingBalance ?? 0));
  const [msg, setMsg] = useState('');
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);

  const save = async (e: React.FormEvent) => {
    e.preventDefault(); setMsg(''); setErr(''); setBusy(true);
    try {
      await api.post('/api/family/profile', {
        name: name.trim(),
        startingBalance: Number(bal.replace(',', '.')) || 0,
      });
      setMsg('Сохранено');
      auth.refresh();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h3>Профиль</h3>
          <p className="card-sub">Имя и стартовый баланс на счетах</p>
        </div>
      </div>

      <div className="profile-row">
        <div className="avatar-lg">{initials(auth.user?.name)}</div>
        <div className="profile-info">
          <b>{auth.user?.name || 'Без имени'}</b>
          <span>{auth.user?.email}</span>
        </div>
      </div>

      <form onSubmit={save}>
        <label>Имя
          <input value={name} onChange={e => setName(e.target.value)} required placeholder="Как вас зовут?" />
        </label>
        <label>Стартовый баланс на счетах, ₽
          <input inputMode="decimal" value={bal} onChange={e => setBal(e.target.value)} placeholder="0" />
        </label>
        <div className="muted" style={{ marginTop: -4, marginBottom: 12 }}>
          Стартовый баланс используется для расчёта общего капитала, если у вас ещё нет счетов.
        </div>
        {err && <div className="alert alert-danger">{err}</div>}
        {msg && <div className="alert alert-info">{msg}</div>}
        <div className="action-row">
          <button className="btn primary" type="submit" disabled={busy}>
            <Icon name="check" size={16} /> {busy ? 'Сохраняем…' : 'Сохранить'}
          </button>
          <button type="button" className="btn secondary" onClick={auth.logout}>
            <Icon name="logout" size={16} /> Выйти
          </button>
        </div>
      </form>
    </div>
  );
}

function AppearanceCard({ theme }: { theme: ThemeController }) {
  const options: { value: ThemeMode; icon: IconName; label: string }[] = [
    { value: 'light', icon: 'sun', label: 'Светлая' },
    { value: 'dark', icon: 'moon', label: 'Тёмная' },
    { value: 'system', icon: 'monitor', label: 'Системная' },
  ];
  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h3>Оформление</h3>
          <p className="card-sub">Тема применяется сразу и запоминается на этом устройстве</p>
        </div>
      </div>
      <div className="theme-options">
        {options.map(o => (
          <button
            key={o.value}
            type="button"
            className={`theme-option ${theme.mode === o.value ? 'active' : ''}`}
            onClick={() => theme.setMode(o.value)}
          >
            <Icon name={o.icon} size={22} />
            {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}

function CategoriesCard({ categories, onChanged }: { categories: Category[]; onChanged: () => void }) {
  const [name, setName] = useState('');
  const [type, setType] = useState<0 | 1>(1);
  const [color, setColor] = useState('#007aff');
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
      <div className="card-head">
        <div>
          <h3>Категории</h3>
          <p className="card-sub">{categories.length} {plural(categories.length, 'категория', 'категории', 'категорий')} для доходов и расходов</p>
        </div>
      </div>

      <div style={{ maxHeight: 280, overflowY: 'auto', marginBottom: 6 }}>
        {categories.map(c => (
          <div className="cat-row" key={c.id}>
            <span className="cat-dot" style={{ background: c.color }} />
            <span className="cat-name">{c.name}</span>
            <span className={`cat-type ${c.type === 0 ? 'income' : ''}`}>
              {c.type === 0 ? 'доход' : 'расход'}
            </span>
            <button
              type="button"
              className="btn icon danger"
              style={{ width: 30, height: 30 }}
              onClick={() => del(c.id)}
              title="Удалить категорию"
              aria-label={`Удалить категорию ${c.name}`}
            >
              <Icon name="x" size={14} />
            </button>
          </div>
        ))}
        {categories.length === 0 && <div className="muted">Категорий пока нет.</div>}
      </div>

      <form onSubmit={add} className="account-form">
        <h4>Новая категория</h4>
        <div className="form-row">
          <label>Название
            <input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Бензин" />
          </label>
          <label>Тип
            <select value={type} onChange={e => setType(Number(e.target.value) as 0 | 1)}>
              <option value={1}>Расход</option>
              <option value={0}>Доход</option>
            </select>
          </label>
          <label style={{ flex: '0 0 80px' }}>Цвет
            <input type="color" value={color} onChange={e => setColor(e.target.value)} />
          </label>
        </div>
        {err && <div className="alert alert-danger">{err}</div>}
        <button className="btn primary" type="submit">
          <Icon name="plus" size={16} /> Добавить
        </button>
      </form>
    </div>
  );
}
