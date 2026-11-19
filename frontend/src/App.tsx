import { useEffect, useRef, useState } from 'react';
import { useAuth } from './auth';
import { api, Category, AccountInfo, UpdateCheck, SelfUpdateStatus, BankInfo } from './api';
import LoginPage from './LoginPage';
import { Dashboard, Journal, QuickTxModal } from './components';
import { GoalsPage } from './GoalsPage';
import { AccountsPage, FamilyCard } from './AccountsPage';
import { InvestmentsPage } from './InvestmentsPage';
import { DebtsPage } from './DebtsPage';
import { ObligationsPage } from './ObligationsPage';
import { CalendarPage } from './CalendarPage';
import { AccountRulesCard } from './AccountRulesCard';
import { useTheme, type ThemeController, type ThemeMode } from './theme';
import { ToastProvider } from './toasts';
import { DialogProvider, useDialog } from './dialogs';
import { Icon, type IconName } from './icons';
import { plural } from './plural';
import { getBankCardStyle, setBankCardStyle, type BankCardStyle } from './bankstyle';
import { loadWidgets, saveWidgets, type DashboardWidgets } from './widgets';

type Route = '' | 'journal' | 'budget' | 'goals' | 'accounts' | 'debts' | 'obligations' | 'calendar' | 'investments' | 'settings' | 'login';

const ROUTES: Route[] = ['', 'journal', 'budget', 'goals', 'accounts', 'debts', 'obligations', 'calendar', 'investments', 'settings'];

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
  { to: 'debts', label: 'Долги', icon: 'credit-card' },
  { to: 'obligations', label: 'Платежи', icon: 'calendar' },
  { to: 'calendar', label: 'Календарь', icon: 'calendar-clock' },
  { to: 'investments', label: 'Инвестиции', icon: 'trending-up' },
  { to: 'settings', label: 'Настройки', icon: 'settings' },
];

const PAGE_SUBTITLE: Record<Route, string> = {
  '': '',
  journal: 'История доходов и расходов',
  budget: 'Месячные лимиты и контроль трат',
  goals: 'Копилки, сроки и ежемесячные отчисления',
  accounts: 'Карты, наличные и вклады',
  debts: 'Кому должны вы и кто должен вам',
  obligations: 'Регулярные траты и сроки оплаты',
  calendar: 'Все важные даты и напоминания',
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
function TopBar({ route, theme, onAdd, onMenu, user, hasUpdate, onLogout, onGoSettings }: {
  route: Route; theme: ThemeController; onAdd: () => void; onMenu: () => void;
  user: { name?: string; email?: string } | null; hasUpdate: boolean;
  onLogout: () => void; onGoSettings: () => void;
}) {
  const [menu, setMenu] = useState(false);
  const menuRef = useRef<HTMLDivElement>(null);

  // Закрываем меню по клику вне и по Escape.
  useEffect(() => {
    if (!menu) return;
    const onDown = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setMenu(false);
    };
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setMenu(false); };
    document.addEventListener('mousedown', onDown);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDown);
      document.removeEventListener('keydown', onKey);
    };
  }, [menu]);

  const title = route === 'budget' ? 'Бюджет и лимиты'
    : route === 'journal' ? 'Операции'
      : route === 'goals' ? 'Цели'
        : route === 'accounts' ? 'Счета'
          : route === 'debts' ? 'Долги'
            : route === 'obligations' ? 'Обязательные платежи'
            : route === 'calendar' ? 'Календарь событий'
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
        <div className="profile-wrap" ref={menuRef}>
          <button
            type="button"
            className="avatar"
            title={user?.name ?? 'Профиль'}
            aria-haspopup="menu"
            aria-expanded={menu}
            onClick={() => setMenu(v => !v)}
          >
            {initials(user?.name)}
          </button>

          {menu && (
            <div className="profile-menu" role="menu">
              <div className="profile-menu-head">
                <div className="profile-menu-name">{user?.name || 'Профиль'}</div>
                {user?.email && <div className="profile-menu-sub">{user.email}</div>}
              </div>
              <button
                type="button"
                className="profile-menu-item"
                role="menuitem"
                onClick={() => { setMenu(false); onGoSettings(); }}
              >
                <Icon name="user" size={15} /> Профиль и настройки
              </button>
              <button
                type="button"
                className="profile-menu-item profile-menu-item--danger"
                role="menuitem"
                onClick={() => { setMenu(false); onLogout(); }}
              >
                <Icon name="logout" size={15} /> Выйти из профиля
              </button>
            </div>
          )}
        </div>
      </div>
    </header>
  );
}

// ---------- Приложение ----------
export default function App() {
  const auth = useAuth();
  const theme = useTheme(auth.user?.theme);
  const [route, setRoute] = useHashRoute();
  const [showQuick, setShowQuick] = useState(false);
  const [presetAccountId, setPresetAccountId] = useState<number | null>(null);
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
      // family=true — чтобы в окне операции были видны и счета членов семьи.
      api.get<AccountInfo[]>('/api/accounts?family=true').then(setAccounts).catch(() => {});
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
      : route === 'debts' ? <DebtsPage key={tick} />
        : route === 'obligations' ? <ObligationsPage key={tick} />
        : route === 'calendar' ? <CalendarPage key={tick} />
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
            : <Dashboard key={tick}
                onAdd={() => { setPresetAccountId(null); setShowQuick(true); }}
                onFamilyTx={(id) => { setPresetAccountId(id); setShowQuick(true); }}
              />;

  return (
    <ToastProvider>
    <DialogProvider>
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
            onAdd={() => { setPresetAccountId(null); setShowQuick(true); }}
            onMenu={() => setMobileOpen(true)}
            hasUpdate={!!update?.updateAvailable}
            onLogout={() => { setPresetAccountId(null); auth.logout(); }}
            onGoSettings={() => setRoute('settings')}
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
    </DialogProvider>
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
      {auth.user?.isMaster && <AdminUsersCard />}
      <div className="settings-grid">
        <div className="settings-col">
          <ProfileCard auth={auth} />
          <FamilyCard />
        </div>
        <div className="settings-col">
          <AppearanceCard theme={theme} />
          <WidgetsCard />
          <CategoriesCard categories={categories} onChanged={() => { onReload(); auth.refresh(); }} />
        </div>
      </div>
      <BanksCard />
      <ExportCard />
      <AuditCard />
      <AccountRulesCard />
    </>
  );
}

function ExportCard() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState(false);
  // B4: параметры экспорта запоминаются между посещениями настроек.
  const readParam = (key: string, fallback: string): string => {
    try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; }
  };
  const writeParam = (key: string, value: string) => {
    try { localStorage.setItem(key, value); } catch { /* приватный режим */ }
  };
  const [from, setFrom] = useState(() => readParam('sk_export_from', ''));
  const [to, setTo] = useState(() => readParam('sk_export_to', ''));
  const [anonymize, setAnonymize] = useState(() => readParam('sk_export_anonymize', '0') === '1');
  const [take, setTake] = useState(() => readParam('sk_export_take', ''));

  const download = async () => {
    if (from && to && from > to) {
      setError('Дата «С» должна быть не позже даты «По».');
      return;
    }
    setBusy(true);
    setError('');
    setSaved(false);
    try {
      const params = new URLSearchParams();
      if (from) params.set('from', from);
      if (to) params.set('to', to);
      if (anonymize) params.set('anonymize', 'true');
      if (take) params.set('take', take);
      const query = params.toString();
      await api.download(`/api/export/profile${query ? `?${query}` : ''}`, 'schitayka-profile.json');
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
          <p className="card-sub">Профиль одним JSON-файлом</p>
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

      <div className="export-params">
        <label>
          <span>С</span>
          <input type="date" value={from} onChange={e => {
            setFrom(e.target.value); writeParam('sk_export_from', e.target.value);
          }} />
        </label>
        <label>
          <span>По</span>
          <input type="date" value={to} onChange={e => {
            setTo(e.target.value); writeParam('sk_export_to', e.target.value);
          }} />
        </label>
        <label>
          <span>Операций</span>
          <select value={take} onChange={e => {
            setTake(e.target.value); writeParam('sk_export_take', e.target.value);
          }}>
            <option value="">все (до 5000)</option>
            <option value="500">500</option>
            <option value="2000">2000</option>
            <option value="5000">5000</option>
          </select>
        </label>
        <label className="export-check">
          <input type="checkbox" checked={anonymize} onChange={e => {
            setAnonymize(e.target.checked); writeParam('sk_export_anonymize', e.target.checked ? '1' : '0');
          }} />
          <span>Обезличить</span>
        </label>
      </div>

      <p className="muted export-note">
        В файл попадают только ваши данные: без пароля, JWT, кода семьи и чужих семейных счетов.
        Период и лимит ограничивают выборку операций (не более 5000). При обезличивании имя, email,
        комментарии и названия заменяются нейтральными значениями — суммы и даты остаются.
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

/** B10: журнал чувствительных действий (экспорт, импорт, смена пароля). */
interface AuditRow {
  id: number; action: string; details: string; ipAddress?: string | null; createdAt: string;
}
const AUDIT_NAMES: Record<string, string> = {
  'export.profile': 'Экспорт профиля',
  'import.receipt': 'Импорт чека',
  'import.receipt.text': 'Импорт текста чека',
  'auth.password-changed': 'Смена пароля',
  'auth.password-failed': 'Неудачная попытка смены пароля',
};

function AuditCard() {
  const [rows, setRows] = useState<AuditRow[] | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    api.get<AuditRow[]>('/api/audit?take=20').then(setRows).catch(e => setError(e.message));
  }, []);

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h3>Журнал безопасности</h3>
          <p className="card-sub">Экспорт и импорт данных, смена пароля</p>
        </div>
      </div>
      {error && <div className="alert alert-warn">{error}</div>}
      {rows === null && !error && <div className="muted">Загружаем…</div>}
      {rows?.length === 0 && <div className="muted">Записей пока нет.</div>}
      {!!rows?.length && (
        <div className="audit-list">
          {rows.map(r => (
            <div key={r.id} className="audit-row">
              <span className="audit-action">{AUDIT_NAMES[r.action] ?? r.action}</span>
              <span className="audit-details">{r.details}</span>
              <span className="audit-time">{new Date(r.createdAt).toLocaleString('ru-RU')}</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function UpdateCard({ update, busy, error, onCheck }: {
  update: UpdateCheck | null; busy: boolean; error: string; onCheck: () => void;
}) {
  const [copied, setCopied] = useState(false);
  const [apply, setApply] = useState<SelfUpdateStatus | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [applyError, setApplyError] = useState('');
  const current = update?.current;
  const latest = update?.latest;
  const repoUrl = update?.repoUrl ?? 'https://github.com/NikolaYudin/schitayka-finance';
  const updateCmd = 'cd /volume1/docker/schitayka && git pull --ff-only && sh deploy/xpenology/update-inplace.sh --no-pull';

  // Состояние самообновления: подтягиваем сразу и далее опрашиваем, пока идёт процесс.
  const loadApply = async () => {
    try { setApply(await api.get<SelfUpdateStatus>('/api/version/apply/status')); }
    catch { /* игнорируем */ }
  };

  useEffect(() => { loadApply(); }, []);

  useEffect(() => {
    if (!apply?.running) return;
    const timer = window.setInterval(loadApply, 2000);
    return () => window.clearInterval(timer);
  }, [apply?.running]);

  const startUpdate = async () => {
    setConfirmOpen(false);
    setApplyError('');
    try {
      await api.post('/api/version/apply', { version: latest?.version ?? null, restart: true });
      await loadApply();
    } catch (ex: any) {
      setApplyError(ex.message || 'Не удалось запустить обновление');
    }
  };

  const when = (value?: string | null) => value
    ? new Date(value).toLocaleString('ru-RU', {
      day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit',
    })
    : '—';

  // B21: название текущего шага берём из статуса, процент показывает сервер.
  const stepName = (status?: string | null): string => {
    switch (status) {
      case 'Running': return 'Подготовка обновления';
      case 'Downloading': return 'Скачивание сборки';
      case 'Verifying': return 'Проверка контрольной суммы';
      case 'Extracting': return 'Распаковка архива';
      case 'BackingUp': return 'Резервная копия текущих файлов';
      case 'Applying': return 'Замена файлов приложения';
      case 'Restarting': return 'Перезапуск приложения';
      case 'Done': return 'Обновление завершено';
      case 'Failed': return 'Обновление прервано';
      default: return 'Ожидание';
    }
  };
  const updateProgress = Math.min(100, Math.max(0, Math.round(apply?.progress ?? 0)));

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

      {/* ---------- Автоматическое обновление ---------- */}
      {apply && apply.enabled && (
        <div className="self-update">
          <div className="self-update-head">
            <span><Icon name="download" size={15} /> Автоматическое обновление</span>
            <span className="badge badge-muted">{stepName(apply.status)}</span>
          </div>

          {(apply.running || apply.status === 'Done') && (
            <>
              <div
                className="progress self-update-progress"
                style={{ marginTop: 8 }}
                role="progressbar"
                aria-valuemin={0}
                aria-valuemax={100}
                aria-valuenow={updateProgress}
              >
                <div style={{ width: `${updateProgress}%` }} />
              </div>
              <div className="muted self-update-step" style={{ marginTop: 6 }}>
                <b>{stepName(apply.status)}</b> · {updateProgress}%
                {apply.targetVersion ? ` · v${apply.targetVersion}` : ''}
              </div>
            </>
          )}

          {apply.status === 'Done' && !apply.running && (
            <div className="rec rec-good" style={{ marginTop: 8 }}>
              <span className="rec-icon"><Icon name="check" size={16} /></span>
              <span>Обновление до версии {apply.targetVersion} применено, база сохранена. Приложение перезапускается — обновите страницу, если оно не вернулось автоматически.</span>
            </div>
          )}

          {apply.status === 'Failed' && !apply.running && (
            <div className="alert alert-danger" style={{ marginTop: 8 }}>
              Не удалось обновиться: {apply.error}
              <div className="muted" style={{ marginTop: 4 }}>Прогресс сброшен. Повторите попытку после устранения причины ошибки.</div>
            </div>
          )}

          {applyError && <div className="alert alert-danger" style={{ marginTop: 8 }}>{applyError}</div>}

          {(apply.log?.length ?? 0) > 0 && (
            <pre className="update-log">{(apply.log ?? []).slice(-8).join('\n')}</pre>
          )}

          {apply.canUpdate && !apply.running && (
            confirmOpen ? (
              <div className="action-row" style={{ marginTop: 10 }}>
                <button className="btn primary" type="button" onClick={startUpdate}>
                  <Icon name="download" size={15} /> Скачать и установить {latest?.version ? `v${latest.version}` : 'обновление'}
                </button>
                <button className="btn secondary" type="button" onClick={() => setConfirmOpen(false)}>Отмена</button>
              </div>
            ) : (
              <div className="action-row" style={{ marginTop: 10 }}>
                <button className="btn primary" type="button" onClick={() => setConfirmOpen(true)}>
                  <Icon name="download" size={16} /> Обновить сейчас
                </button>
              </div>
            )
          )}

          {confirmOpen && (
            <p className="muted update-note" style={{ marginTop: 6 }}>
              Приложение скачает сборку {latest?.version ? `v${latest.version}` : ''} из GitHub Releases,
              проверит контрольную сумму, сохранит резервную копию текущих файлов и перезапустится.
              База данных в томе <b>schitayka-data</b> не затрагивается.
            </p>
          )}

          {apply.enabled && !apply.canUpdate && (
            <p className="muted update-note" style={{ marginTop: 6 }}>
              Обновлять может только администратор экземпляра (роль Master или e-mail из списка SelfUpdate:AdminEmails).
            </p>
          )}
        </div>
      )}

      {apply && !apply.enabled && (
        <p className="muted update-note" style={{ marginTop: 8 }}>
          Автоматическое обновление выключено. Включается переменной <code>SelfUpdate__Enabled=true</code> в <code>.env</code>.
        </p>
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

/** Управление пользователями семьи (мастер-профиль → администратор). */
interface AdminUser {
  id: number; email: string; name: string; role: string;
  hasPassword: boolean; isFamily: boolean; createdAt: string;
}

function AdminUsersCard() {
  const dlg = useDialog();
  const [rows, setRows] = useState<AdminUser[]>([]);
  const [email, setEmail] = useState('');
  const [name, setName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const reload = () => api.get<AdminUser[]>('/api/admin/users')
    .then(setRows).catch(e => setError(e.message));
  useEffect(() => { reload(); }, []);

  const create = async (e: React.FormEvent) => {
    e.preventDefault(); setError(''); setBusy(true);
    try {
      await api.post('/api/admin/users', { email, name, password });
      setEmail(''); setName(''); setPassword('');
      await dlg.alert('Пользователь создан и добавлен в семью.');
      reload();
    } catch (err: any) { setError(err.message); } finally { setBusy(false); }
  };

  const reset = async (u: AdminUser) => {
    const next = await dlg.prompt(`Новый пароль для ${u.email} (не короче 8 символов)`, '', 'Сброс пароля');
    if (!next) return;
    if (next.length < 8) { await dlg.alert('Пароль должен быть не короче 8 символов'); return; }
    try {
      await api.post(`/api/admin/users/${u.id}/reset-password`, { password: next });
      await dlg.alert('Пароль сброшен. Сессии пользователя отключены.');
      reload();
    } catch (err: any) { setError(err.message); }
  };

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h3>Пользователи семьи</h3>
          <p className="card-sub">Администратор создаёт записи и сбрасывает пароли</p>
        </div>
        <span className="badge badge-muted">{rows.length}</span>
      </div>

      <div className="admin-users">
        {rows.map(u => (
          <div key={u.id} className="admin-user-row">
            <span className="member-avatar">{initials(u.name)}</span>
            <span className="admin-user-info">
              <b>{u.name}</b>
              <span>{u.email}{u.isFamily ? ' · семья' : ''}</span>
            </span>
            <span className="badge badge-muted">{u.role === 'Master' ? 'Администратор' : 'Участник'}</span>
            <button className="btn small secondary" type="button" onClick={() => reset(u)}>
              Сбросить пароль
            </button>
          </div>
        ))}
        {rows.length === 0 && <div className="muted">Пользователей пока нет.</div>}
      </div>

      <form onSubmit={create} style={{ marginTop: 14, borderTop: '1px solid var(--border)', paddingTop: 14 }}>
        <h4>Добавить пользователя</h4>
        <label>Email<input type="email" value={email} onChange={e => setEmail(e.target.value)} required /></label>
        <label>Имя<input value={name} onChange={e => setName(e.target.value)} required /></label>
        <label>Пароль<input type="password" value={password} onChange={e => setPassword(e.target.value)}
          minLength={8} required autoComplete="new-password" /></label>
        {error && <div className="alert alert-danger">{error}</div>}
        <div className="action-row">
          <button className="btn primary" type="submit" disabled={busy}>
            <Icon name="plus" size={16} /> {busy ? 'Создаём…' : 'Создать пользователя'}
          </button>
        </div>
      </form>
    </div>
  );
}

function ProfileCard({ auth }: { auth: ReturnType<typeof useAuth> }) {
  const dlg = useDialog();
  const [name, setName] = useState(auth.user?.name ?? '');
  const [bal, setBal] = useState(String(auth.user?.startingBalance ?? 0));
  const [msg, setMsg] = useState('');
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [curPwd, setCurPwd] = useState('');
  const [newPwd, setNewPwd] = useState('');
  const [newPwd2, setNewPwd2] = useState('');
  const [pwdMsg, setPwdMsg] = useState('');
  const [pwdBusy, setPwdBusy] = useState(false);

  const changePwd = async (e: React.FormEvent) => {
    e.preventDefault(); setPwdMsg('');
    if (newPwd !== newPwd2) { setPwdMsg('Новые пароли не совпадают'); return; }
    if (newPwd.length < 8) { setPwdMsg('Новый пароль должен быть не короче 8 символов'); return; }
    setPwdBusy(true);
    try {
      await api.post('/api/auth/change-password', {
        currentPassword: curPwd, newPassword: newPwd,
      });
      setCurPwd(''); setNewPwd(''); setNewPwd2('');
      await dlg.alert('Пароль изменён. Старые сессии отключены — войдите заново.');
      auth.logout();
    } catch (ex: any) { setPwdMsg(ex.message); } finally { setPwdBusy(false); }
  };

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

      {auth.user?.hasPassword === false ? (
        <div className="muted" style={{ marginTop: 16 }}>
          Вы входите через Яндекс — пароль у аккаунта не установлен.
        </div>
      ) : (
        <form onSubmit={changePwd} style={{ marginTop: 20, borderTop: '1px solid var(--border)', paddingTop: 16 }}>
          <h4>Смена пароля</h4>
          <label>Текущий пароль
            <input type="password" autoComplete="current-password" value={curPwd}
              onChange={e => setCurPwd(e.target.value)} required />
          </label>
          <label>Новый пароль
            <input type="password" autoComplete="new-password" value={newPwd}
              onChange={e => setNewPwd(e.target.value)} minLength={8} required />
          </label>
          <label>Повторите новый пароль
            <input type="password" autoComplete="new-password" value={newPwd2}
              onChange={e => setNewPwd2(e.target.value)} minLength={8} required />
          </label>
          {pwdMsg && <div className="alert alert-danger">{pwdMsg}</div>}
          <div className="action-row">
            <button className="btn primary" type="submit" disabled={pwdBusy}>
              <Icon name="check" size={16} /> {pwdBusy ? 'Меняем…' : 'Сменить пароль'}
            </button>
          </div>
        </form>
      )}
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

function WidgetsCard() {
  const [widgets, setWidgets] = useState<DashboardWidgets>(loadWidgets);

  const toggle = (key: keyof DashboardWidgets) => {
    const next = { ...widgets, [key]: !widgets[key] };
    setWidgets(next);
    saveWidgets(next);
  };

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h3>Виджеты дашборда</h3>
          <p className="card-sub">Какие блоки показывать на главной странице</p>
        </div>
      </div>
      <div className="cat-row">
        <span className="cat-name">Инвестиции</span>
        <button
          type="button"
          className={`switch ${widgets.investments ? 'on' : ''}`}
          onClick={() => toggle('investments')}
          title={widgets.investments ? 'Скрыть блок' : 'Показать блок'}
          aria-label="Показывать блок «Инвестиции» на дашборде"
        />
      </div>
      <div className="cat-row">
        <span className="cat-name">Долги</span>
        <button
          type="button"
          className={`switch ${widgets.debts ? 'on' : ''}`}
          onClick={() => toggle('debts')}
          title={widgets.debts ? 'Скрыть блок' : 'Показать блок'}
          aria-label="Показывать блок «Долги» на дашборде"
        />
      </div>
      <div className="cat-row">
        <span className="cat-name">Обязательные платежи</span>
        <button
          type="button"
          className={`switch ${widgets.obligations ? 'on' : ''}`}
          onClick={() => toggle('obligations')}
          title={widgets.obligations ? 'Скрыть блок' : 'Показать блок'}
          aria-label="Показывать блок «Обязательные платежи» на дашборде"
        />
      </div>
      <div className="cat-row">
        <span className="cat-name">Ближайшие события</span>
        <button
          type="button"
          className={`switch ${widgets.calendar ? 'on' : ''}`}
          onClick={() => toggle('calendar')}
          title={widgets.calendar ? 'Скрыть блок' : 'Показать блок'}
          aria-label="Показывать блок «Календарь событий» на дашборде"
        />
      </div>
      <p className="muted" style={{ marginTop: 8, fontSize: 12 }}>
        Блоки появляются, когда есть данные: портфели, активные долги, платежи и события.
      </p>
    </div>
  );
}

function CategoriesCard({ categories, onChanged }: { categories: Category[]; onChanged: () => void }) {
  const dlg = useDialog();
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
    if (!await dlg.confirm('Удалить категорию?')) return;
    try { await api.del(`/api/categories/${id}`); onChanged(); }
    catch (ex: any) { await dlg.alert(ex.message); }
  };

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h3>Категории</h3>
          <p className="card-sub">
            {categories.length} {plural(categories.length, 'категория', 'категории', 'категорий')} для доходов и расходов
            {categories.some(c => c.familyShared) ? ' · 🏠 общие для семьи' : ''}
          </p>
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
            {c.familyShared && (
              <span className="badge badge-muted" title="Общая категория семьи" style={{ fontSize: 10 }}>🏠</span>
            )}
            {!c.isSystem && (
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
            )}
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

/** Банки (0.3/N5): справочник основных банков РФ + свои банки; выбор банка — в форме счета. */
function BanksCard() {
  const dlg = useDialog();
  const [banks, setBanks] = useState<BankInfo[]>([]);
  const [search, setSearch] = useState('');
  const [name, setName] = useState('');
  const [color, setColor] = useState('#0a84ff');
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [cardStyle, setCardStyle] = useState<BankCardStyle>(() => getBankCardStyle());
  const [hideUnused, setHideUnused] = useState(false);

  const load = () => {
    api.get<BankInfo[]>('/api/banks').then(b => setBanks(b)).catch(e => setErr(e.message));
  };
  useEffect(load, []);

  const add = async (e: React.FormEvent) => {
    e.preventDefault(); setErr(''); setBusy(true);
    try {
      await api.post('/api/banks', { name, brandColor: color });
      setName(''); load();
    } catch (ex: any) { setErr(ex.message); }
    finally { setBusy(false); }
  };
  const del = async (b: BankInfo) => {
    if (!await dlg.confirm(`Удалить банк «${b.name}» из списка?`)) return;
    try { await api.del(`/api/banks/${b.id}`); load(); }
    catch (ex: any) { await dlg.alert(ex.message); }
  };
  const removeUnusedOwn = async () => {
    const count = banks.filter(b => !b.isSystem && !b.inUse).length;
    if (count === 0) { await dlg.alert('Нет неиспользуемых своих банков для удаления.'); return; }
    if (!await dlg.confirm(`Удалить ${count} ${plural(count, 'неиспользуемый свой банк', 'неиспользуемых своих банка', 'неиспользуемых своих банков')}?`)) return;
    setErr(''); setBusy(true);
    try {
      const r = await api.del<{ removed: number }>('/api/banks/unused');
      load();
      await dlg.alert(`Удалено банков: ${r.removed}`);
    } catch (ex: any) { setErr(ex.message); await dlg.alert(ex.message); }
    finally { setBusy(false); }
  };

  const q = search.trim().toLowerCase();
  const visible = banks.filter(b => {
    if (hideUnused && !b.inUse) return false;
    if (!q) return true;
    return b.name.toLowerCase().includes(q) || (b.shortName ?? '').toLowerCase().includes(q);
  });
  const system = visible.filter(b => b.isSystem);
  const own = visible.filter(b => !b.isSystem);
  const unusedOwnCount = banks.filter(b => !b.isSystem && !b.inUse).length;

  const monogram = (b: BankInfo) => {
    const src = b.shortName?.trim() || b.name.trim();
    return (src[0] ?? '?').toUpperCase();
  };

  const rows = (list: BankInfo[]) => (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(220px, 1fr))', gap: 8 }}>
      {list.map(b => (
        <div
          key={b.id}
          className="cat-row"
          style={{ background: 'var(--fill)', borderRadius: 12, padding: '8px 10px', margin: 0, opacity: b.inUse ? 1 : 0.9 }}
        >
          <span
            aria-hidden
            style={{
              width: 28, height: 28, borderRadius: 8, flex: '0 0 28px',
              display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
              background: b.brandColor ?? 'var(--muted)',
              color: '#fff', fontSize: 13, fontWeight: 700,
            }}
          >
            {monogram(b)}
          </span>
          <span className="cat-name" style={{ fontSize: 13, minWidth: 0, flex: 1 }}>
            <span style={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{b.name}</span>
            <span style={{ display: 'inline-flex', gap: 6, flexWrap: 'wrap', marginTop: 2 }}>
              {!b.isSystem && <span className="badge badge-muted" style={{ fontSize: 11 }}>свой</span>}
              <span className={`badge ${b.inUse ? 'badge-success' : 'badge-muted'}`} style={{ fontSize: 11 }}>
                {b.inUse ? 'используется' : 'не используется'}
              </span>
            </span>
          </span>
          {!b.isSystem && !b.inUse && (
            <button
              type="button"
              className="btn icon danger"
              style={{ width: 30, height: 30, marginLeft: 'auto', flex: '0 0 30px' }}
              onClick={() => del(b)}
              title="Удалить неиспользуемый банк"
              aria-label={`Удалить банк ${b.name}`}
            >
              <Icon name="trash" size={14} />
            </button>
          )}
        </div>
      ))}
    </div>
  );

  return (
    <div className="card">
      <div className="card-head" style={{ alignItems: 'flex-start', gap: 12, flexWrap: 'wrap' }}>
        <div>
          <h3>Банки</h3>
          <p className="card-sub">
            {banks.length} {plural(banks.length, 'банк', 'банка', 'банков')} ·
            привязка к счетам — в форме счёта на вкладке «Счета»
          </p>
        </div>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center', marginLeft: 'auto' }}>
          <label style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 13, cursor: 'pointer', userSelect: 'none' }}>
            <input type="checkbox" checked={hideUnused} onChange={e => setHideUnused(e.target.checked)} />
            Скрыть неиспользуемые
          </label>
          <div style={{ position: 'relative' }}>
            <input
              value={search}
              onChange={e => setSearch(e.target.value)}
              placeholder="Поиск банка…"
              style={{ paddingLeft: 30, minWidth: 190 }}
              aria-label="Поиск банка"
            />
            <span style={{ position: 'absolute', left: 9, top: 8, color: 'var(--muted)' }}>
              <Icon name="search" size={15} />
            </span>
          </div>
        </div>
      </div>

      <div style={{ maxHeight: 320, overflowY: 'auto', marginBottom: 10 }}>
        {own.length > 0 && <div style={{ marginBottom: 10 }}><div className="muted" style={{ marginBottom: 6 }}>Свои банки</div>{rows(own)}</div>}
        {system.length > 0 && <div><div className="muted" style={{ marginBottom: 6 }}>Справочник банков</div>{rows(system)}</div>}
        {visible.length === 0 && <div className="muted">Ничего не найдено.</div>}
      </div>

      <div className="action-row" style={{ marginBottom: 14, flexWrap: 'wrap', alignItems: 'center' }}>
        <button type="button" className="btn secondary" onClick={removeUnusedOwn} disabled={unusedOwnCount === 0 || busy}>
          <Icon name="trash" size={14} /> Удалить неиспользуемые свои банки{unusedOwnCount > 0 ? ` (${unusedOwnCount})` : ''}
        </button>
        <span className="muted" style={{ fontSize: 12 }}>Системные банки нельзя удалить; неиспользуемые можно скрыть из списка фильтром.</span>
      </div>

      {/* 0.3/N6: переключатель фирменного стиля карточек банков */}
      <div className="form-row" style={{ alignItems: 'center', gap: 10, marginBottom: 14 }}>
        <label style={{ flex: 1, marginBottom: 0 }}>
          Стиль карточек банков
          <span style={{ display: 'block', fontWeight: 400, fontSize: 12, color: 'var(--muted)', marginTop: 2 }}>
            «Фирменный» — цвет и монограмма банка на карточке счёта (Дашборд и вкладка «Счета»)
          </span>
        </label>
        <div className="segmented" role="group" aria-label="Стиль карточек банков">
          {([
            ['neutral', 'Нейтральный'],
            ['brand', 'Фирменный'],
          ] as const).map(([mode, label]) => (
            <button
              key={mode}
              type="button"
              className={`segment ${cardStyle === mode ? 'active' : ''}`}
              onClick={() => { setCardStyle(mode); setBankCardStyle(mode); }}
            >
              {label}
            </button>
          ))}
        </div>
      </div>

      <form onSubmit={add} className="account-form">
        <h4>Свой банк</h4>
        <div className="form-row">
          <label style={{ flex: 3 }}>
            Название
            <input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Мой банк" />
          </label>
          <label style={{ flex: '0 0 80px' }}>
            Цвет
            <input type="color" value={color} onChange={e => setColor(e.target.value)} />
          </label>
        </div>
        {err && <div className="alert alert-danger">{err}</div>}
        <button className="btn primary" type="submit" disabled={busy}>
          <Icon name="plus" size={16} /> Добавить банк
        </button>
      </form>
    </div>
  );
}
