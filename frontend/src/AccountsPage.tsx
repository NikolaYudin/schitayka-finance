import { useEffect, useState, type CSSProperties } from 'react';
import {
  AccountInfo, AccountType, FamilyMember, AccountRule, AccountAlert, ReserveLoan, BankInfo,
  api, fmt, Transfer,
} from './api';
import { Icon } from './icons';

const TYPE_HINT: Record<AccountType, string> = {
  0: 'Дебетовая карта',
  1: 'Кредитная карта',
  2: 'Наличные',
  3: 'Вклад / накопительный',
  4: 'Прочее',
};

/** Мягкий оттенок цвета счёта для декоративных элементов. */
const tint = (color: string) => (/^#[0-9a-f]{6}$/i.test(color) ? `${color}1f` : 'var(--accent-soft)');

function accountInitial(name: string): string {
  return name.trim().charAt(0).toUpperCase() || 'С';
}

// ---- Страница «Счета»: карточки в сетке + форма добавления/редактирования ----
export function AccountsPage({ onChanged }: { onChanged?: () => void }) {
  const [list, setList] = useState<AccountInfo[] | null>(null);
  const [err, setErr] = useState('');
  const [editing, setEditing] = useState<AccountInfo | null>(null); // null = нет формы, объект (id 0) = новая
  const [showArchived, setShowArchived] = useState(false);
  const [familyMode, setFamilyMode] = useState(true);
  const [inFamily, setInFamily] = useState(false);
  const [rules, setRules] = useState<AccountRule[]>([]);
  const [alerts, setAlerts] = useState<AccountAlert[]>([]);
  const [loans, setLoans] = useState<ReserveLoan[]>([]);
  const [transfers, setTransfers] = useState<Transfer[]>([]);
  const [banks, setBanks] = useState<BankInfo[]>([]);

  const load = () => {
    api.get<BankInfo[]>('/api/banks').then(setBanks).catch(() => {});
    api.get<{ familyCode: string | null; members: FamilyMember[] }>('/api/family/members')
      .then(r => setInFamily(!!r.familyCode && r.members.length > 1))
      .catch(() => {});
    api.get<AccountInfo[]>(`/api/accounts?includeArchived=${showArchived}&family=${familyMode}`)
      .then(setList).catch(e => setErr(e.message));
    api.get<Transfer[]>('/api/transfers').then(setTransfers).catch(() => {});
  };
  const loadRules = () => {
    api.get<AccountRule[]>('/api/account-rules').then(setRules).catch(() => {});
  };
  const loadAlerts = () => {
    api.get<AccountAlert[]>('/api/alerts').then(setAlerts).catch(() => {});
  };
  const loadLoans = () => {
    api.get<ReserveLoan[]>('/api/reserve-loans?activeOnly=true').then(setLoans).catch(() => {});
  };
  useEffect(() => { load(); loadRules(); loadAlerts(); loadLoans(); }, [showArchived, familyMode]);

  const cancelTransfer = async (id: number) => {
    if (!confirm('Отменить перевод? Обе его операции будут удалены, остатки вернутся.')) return;
    try { await api.del(`/api/transfers/${id}`); afterChange(); }
    catch (e: any) { alert(e.message); }
  };
  const transfersFor = (accountId: number) =>
    transfers.filter(t => t.fromAccountId === accountId || t.toAccountId === accountId).slice(0, 3);

  const afterChange = () => { load(); onChanged?.(); };

  const del = async (a: AccountInfo) => {
    if (!confirm(`Удалить счёт «${a.name}»? Операции останутся в журнале без счёта.`)) return;
    try { await api.del(`/api/accounts/${a.id}`); afterChange(); }
    catch (e: any) { alert(e.message); }
  };
  const archive = async (a: AccountInfo) => {
    try {
      await api.put(`/api/accounts/${a.id}`, {
        name: a.name, type: a.type, balance: a.startingBalance ?? a.balance,
        creditLimit: a.creditLimit, cashbackRate: a.cashbackRate, color: a.color, archived: !a.isArchived,
        bankId: a.bankId ?? null,
      });
      afterChange();
    } catch (e: any) { alert(e.message); }
  };
  const adjust = async (a: AccountInfo, kind: 'cashback' | 'adjust') => {
    const def = kind === 'cashback' ? '' : '-5000';
    const raw = prompt(kind === 'cashback'
      ? `Сумма зачисленного кэшбэка на «${a.name}», ₽`
      : `Корректировка баланса «${a.name}» (можно со знаком −), ₽`, def);
    if (raw === null) return;
    const amount = Number(raw.replace(',', '.')) || 0;
    if (amount === 0) { alert('Укажите сумму'); return; }
    try {
      if (kind === 'cashback') await api.post(`/api/accounts/${a.id}/cashback`, { amount });
      else await api.post(`/api/accounts/${a.id}/adjust`, { delta: amount });
      afterChange();
    } catch (e: any) { alert(e.message); }
  };

  const visible = (list ?? []).filter(a => !a.isArchived);
  const total = visible.filter(a => a.type !== 1).reduce((s, a) => s + a.balance, 0);
  const debt = visible.filter(a => a.type === 1).reduce((s, a) => s + a.balance, 0);
  const cashback = visible.reduce((s, a) => s + (a.cashbackAccrued || 0), 0);
  const showOwnerBadge = familyMode && inFamily && visible.some(a => (a.ownerName ?? '') !== (visible[0]?.ownerName ?? ''));

  const newAccount = () => setEditing({
    id: 0, name: '', type: 0 as AccountType, typeName: 'Дебетовая карта', balance: 0, startingBalance: 0,
    creditLimit: 0, cashbackRate: 0, cashbackAccrued: 0, color: '#007aff', isArchived: false, available: 0, creditUsagePct: null,
    bankId: null, bankName: null,
  });

  const minRulesFor = (a: AccountInfo) => rules.filter(r => r.kind === 1 && r.isEnabled && r.accountId === a.id);
  const reserveRulesFor = (a: AccountInfo) => rules.filter(r => r.kind === 2 && r.isEnabled && (r.accountId === a.id || r.accountId === null));
  const loansFor = (a: AccountInfo) => loans.filter(l => l.accountId === a.id);
  const minBreached = (a: AccountInfo, r: AccountRule): boolean => {
    const th = r.threshold ?? 0;
    return a.type === 1 ? a.balance > th : a.balance < th;
  };
  const freeOf = (r: AccountRule, a: AccountInfo): number => {
    const taken = loansFor(a).filter(l => l.ruleId === r.id).reduce((s, l) => s + l.amount, 0);
    return Math.max(0, Math.min(a.balance, r.threshold ?? 0) - taken);
  };
  const takeLoan = async (a: AccountInfo, r: AccountRule) => {
    const raw = prompt(`Сколько взять из резерва «${r.name}» на счёт «${a.name}»?`, '1000');
    if (raw === null) return;
    const amount = Number(raw.replace(',', '.')) || 0;
    if (amount <= 0) { alert('Укажите сумму'); return; }
    try { await api.post('/api/reserve-loans', { accountId: a.id, amount, ruleId: r.id }); afterChange(); }
    catch (e: any) { alert(e.message); }
  };
  const returnLoan = async (id: number) => {
    try { await api.post(`/api/reserve-loans/${id}/return`, {}); afterChange(); }
    catch (e: any) { alert(e.message); }
  };

  return (
    <>
      <div className="card">
        <div className="card-head" style={{ flexWrap: 'wrap' }}>
          <div>
            <h3>Мои счета</h3>
            <p className="card-sub">
              {familyMode && inFamily ? 'Все счета семьи в одном месте' : 'Карты, наличные и накопительные счета'}
            </p>
          </div>
          <button className="btn primary" type="button" onClick={newAccount}>
            <Icon name="plus" size={17} strokeWidth={2.3} /> Добавить счёт
          </button>
        </div>

        <div className="metric-box">
          <div className="metric pos"><span>{fmt(total)}</span><small>на счетах</small></div>
          <div className={`metric ${debt > 0 ? 'neg' : ''}`}><span>{fmt(debt)}</span><small>долг по кредиткам</small></div>
          <div className="metric"><span>{fmt(cashback)}</span><small>кэшбэк начислен</small></div>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap', marginTop: 16 }}>
          {inFamily ? (
            <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 0, fontWeight: 500, color: 'var(--muted)' }}>
              <input type="checkbox" checked={familyMode} onChange={e => setFamilyMode(e.target.checked)} />
              показывать счета всей семьи
            </label>
          ) : <span />}
          <label style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 0, fontWeight: 500, color: 'var(--muted)' }}>
            <input type="checkbox" checked={showArchived} onChange={e => setShowArchived(e.target.checked)} />
            показывать архивные
          </label>
        </div>
      </div>

      {err && <div className="card"><div className="alert alert-danger">{err}</div></div>}

      {!list ? (
        <div className="card"><div className="empty"><div className="empty-text">Загрузка счетов…</div></div></div>
      ) : list.length === 0 ? (
        <div className="card">
          <div className="empty">
            <span className="empty-ico"><Icon name="wallet" size={22} /></span>
            <div className="empty-title">Счетов пока нет</div>
            <div className="empty-text">Добавьте карту, наличные или вклад — и приложение посчитает общий баланс.</div>
            <button className="btn primary" type="button" style={{ marginTop: 10 }} onClick={newAccount}>
              <Icon name="plus" size={16} /> Добавить первый счёт
            </button>
          </div>
        </div>
      ) : (
        <div className="accounts-grid">
          {list.map(a => (
            <div className={`account-card ${a.isArchived ? 'is-archived' : ''}`} key={a.id} style={{ '--acc': a.color } as CSSProperties}>
              <div className="account-head">
                <div style={{ display: 'flex', alignItems: 'center', gap: 10, minWidth: 0 }}>
                  <span
                    className="member-avatar"
                    style={{ background: tint(a.color), color: a.color, borderRadius: 13, width: 38, height: 38, flex: '0 0 38px' }}
                  >
                    {accountInitial(a.name)}
                  </span>
                  <div style={{ minWidth: 0 }}>
                    <div className="account-name">{a.name}</div>
                    <div className="account-type">{a.typeName || TYPE_HINT[a.type]}{a.isArchived ? ' · архив' : ''}</div>
                    {a.bankName && <div className="account-type">{a.bankName}</div>}
                  </div>
                </div>
                {showOwnerBadge && <span className="badge badge-muted">{a.ownerName || 'участник'}</span>}
              </div>

              <div className="account-balance">{fmt(a.balance)}</div>

              {a.type === 1 && a.creditLimit > 0 && (
                <div className="limit-row" style={{ marginTop: 4 }}>
                  <div className="limit-head">
                    <span className="limit-vals">Лимит {fmt(a.creditLimit)} · доступно {fmt(a.available)}</span>
                    <span className="limit-vals">{a.creditUsagePct}%</span>
                  </div>
                  <div className="progress">
                    <div
                      className={a.creditUsagePct! > 80 ? 'over' : ''}
                      style={{ width: `${Math.min(100, a.creditUsagePct ?? 0)}%`, background: a.creditUsagePct! > 80 ? undefined : 'var(--orange)' }}
                    />
                  </div>
                </div>
              )}

              {a.type === 0 && a.cashbackRate > 0 && (
                <div className="account-meta">
                  <Icon name="gift" size={12} /> кэшбэк {a.cashbackRate}% · начислено {fmt(a.cashbackAccrued)}
                </div>
              )}

              {a.type === 3 && a.interest && a.interest.interestRate > 0 && (
                <div className="account-meta" style={{ display: 'block' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
                    <Icon name="sparkles" size={12} />
                    <strong>{a.interest.interestRate}% годовых</strong>
                    <span>· {a.interest.interestModeName}</span>
                  </div>
                  <div className="tx-sub" style={{ marginTop: 4 }}>
                    ≈ {fmt(a.interest.interestMonthly)} ₽/мес · {fmt(a.interest.interestYearly)} ₽/год (до налога)
                  </div>
                  <div className="tx-sub">
                    после налога {a.interest.interestTaxRate}%: {fmt(a.interest.interestYearlyNet)} ₽/год
                    {a.interest.daysToMaturity != null && a.interest.daysToMaturity >= 0
                      ? ` · до погашения ${a.interest.daysToMaturity} дн.` : ''}
                  </div>
                </div>
              )}

              {minRulesFor(a).map(r => (
                <div key={r.id} className={`badge-row ${minBreached(a, r) ? 'badge-danger' : ''}`}>
                  <Icon name="shield" size={13} />
                  Неснижаемый остаток: {fmt(r.threshold ?? 0)}
                  {minBreached(a, r) && <span className="badge badge-danger">нарушен</span>}
                </div>
              ))}

              {reserveRulesFor(a).map(r => (
                <div className="limit-row" key={r.id} style={{ marginTop: 4 }}>
                  <div className="limit-head">
                    <span className="limit-vals">Резерв {fmt(r.threshold ?? 0)} · свободно {fmt(freeOf(r, a))}</span>
                    <button className="btn small" onClick={() => takeLoan(a, r)}>Взять</button>
                  </div>
                </div>
              ))}

              {loansFor(a).map(l => (
                <div className="tx-item" key={l.id} style={{ padding: '7px 0' }}>
                  <span className="tx-ico" style={{ width: 30, height: 30, flex: '0 0 30px', color: 'var(--orange)', background: 'var(--orange-soft)' }}>
                    <Icon name="refresh" size={14} />
                  </span>
                  <div className="tx-main">
                    <div className="tx-title" style={{ fontSize: 12.5 }}>Заём: {fmt(l.amount)}</div>
                    <div className="tx-sub">до {new Date(l.dueDate).toLocaleDateString('ru-RU')}{l.isOverdue ? ' · просрочен' : ''}</div>
                  </div>
                  <button className="btn small" onClick={() => returnLoan(l.id)}>Вернул</button>
                </div>
              ))}

              {transfersFor(a.id).map(t => (
                <div className="tx-item" key={`tr-${t.id}`} style={{ padding: '7px 0' }}>
                  <span className="tx-ico" style={{ width: 30, height: 30, flex: '0 0 30px', color: 'var(--accent)', background: 'var(--accent-soft, rgba(0, 122, 255, 0.12))' }}>
                    <Icon name="operations" size={14} />
                  </span>
                  <div className="tx-main">
                    <div className="tx-title" style={{ fontSize: 12.5 }}>
                      {t.fromAccountId === a.id ? '→ ' + t.toAccountName : '← ' + t.fromAccountName}
                      {' '}· {fmt(t.amount)}
                    </div>
                    <div className="tx-sub">
                      {new Date(t.date + 'T00:00:00').toLocaleDateString('ru-RU')}
                      {t.comment ? ` · ${t.comment}` : ''}
                    </div>
                  </div>
                  <button className="btn small danger-ghost" title="Отменить перевод" onClick={() => cancelTransfer(t.id)}>
                    <Icon name="x" size={13} />
                  </button>
                </div>
              ))}

              {alerts.filter(al => al.accountId === a.id).map(al => (
                <div key={al.id} className={`alert-row ${al.level === 'danger' ? 'alert-danger' : 'alert-warn'}`}>
                  <div className="alert-msg">{al.message}</div>
                </div>
              ))}

              <div className="account-actions">
                <button className="btn" onClick={() => setEditing({ ...a })} title="Изменить счёт">
                  <Icon name="pencil" size={14} /> Изменить
                </button>
                {a.type !== 1 && (
                  <button className="btn" title="Зачислить кэшбэк" onClick={() => adjust(a, 'cashback')}>
                    <Icon name="gift" size={14} /> Кэшбэк
                  </button>
                )}
                <button className="btn" title="Корректировать баланс" onClick={() => adjust(a, 'adjust')}>±</button>
                <button className="btn" title={a.isArchived ? 'Вернуть из архива' : 'В архив'} onClick={() => archive(a)}>
                  <Icon name="archive" size={14} />
                </button>
                <button className="btn danger-ghost" title="Удалить счёт" onClick={() => del(a)}>
                  <Icon name="trash" size={14} />
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {editing && (
        <div className="card">
          <AccountForm
            account={editing}
            onClose={() => setEditing(null)}
            onSaved={() => { setEditing(null); afterChange(); }}
          />
        </div>
      )}
    </>
  );
}

// ---- Форма счёта ----
function AccountForm({ account, onClose, onSaved }: {
  account: AccountInfo; onClose: () => void; onSaved: () => void;
}) {
  const isNew = account.id === 0;
  const [name, setName] = useState(account.name);
  const [type, setType] = useState<AccountType>(account.type);
  const [balance, setBalance] = useState(String(account.startingBalance ?? account.balance ?? 0));
  const [limit, setLimit] = useState(String(account.creditLimit || ''));
  const [cashback, setCashback] = useState(account.cashbackRate ? String(account.cashbackRate) : '');
  const [rate, setRate] = useState(account.interest?.interestRate ? String(account.interest.interestRate) : '');
  const [interestMode, setInterestMode] = useState<number>(account.interest?.interestMode ?? 2);
  const [openedAt, setOpenedAt] = useState(account.interest?.openedAt ? account.interest.openedAt.slice(0, 10) : '');
  const [maturesAt, setMaturesAt] = useState(account.interest?.maturesAt ? account.interest.maturesAt.slice(0, 10) : '');
  const [taxRate, setTaxRate] = useState(account.interest?.interestTaxRate != null ? String(account.interest.interestTaxRate) : '13');
  const [color, setColor] = useState(account.color || '#007aff');
  const [bankId, setBankId] = useState<number>(account.bankId ?? 0);
  const [banks, setBanks] = useState<BankInfo[]>([]);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');

  const isCredit = type === 1;
  const isDeposit = type === 3;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setErr(''); setBusy(true);
    const body = {
      name, type,
      balance: Number(balance.replace(',', '.')) || 0,
      creditLimit: isCredit ? (Number(limit.replace(',', '.')) || 0) : 0,
      cashbackRate: isCredit || isDeposit ? 0 : (Number(cashback.replace(',', '.')) || 0),
      bankId: bankId || null,
      interestRate: isDeposit ? (Number(rate.replace(',', '.')) || 0) : 0,
      interestMode: isDeposit ? interestMode : 0,
      openedAt: isDeposit && openedAt ? new Date(openedAt + 'T00:00:00').toISOString() : null,
      maturesAt: isDeposit && maturesAt ? new Date(maturesAt + 'T00:00:00').toISOString() : null,
      interestTaxRate: Number(taxRate.replace(',', '.')) || 13,
      color,
    };
    try {
      if (isNew) await api.post('/api/accounts', body);
      else await api.put(`/api/accounts/${account.id}`, body);
      onSaved();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  return (
    <form onSubmit={submit} className="account-form">
      <div className="card-head" style={{ marginBottom: 14 }}>
        <div>
          <h4 style={{ margin: 0 }}>{isNew ? 'Новый счёт' : `Счёт «${account.name}»`}</h4>
          <p className="card-sub">Название, тип и текущий баланс</p>
        </div>
        <button type="button" className="btn icon" onClick={onClose} aria-label="Закрыть форму">
          <Icon name="x" size={17} />
        </button>
      </div>

      <div className="form-row">
        <label style={{ flex: '2 1 240px' }}>Название
          <input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Зарплатная, Халва, Наличные" />
        </label>
        <label>Тип
          <select value={type} onChange={e => setType(Number(e.target.value) as AccountType)}>
            <option value={0}>Дебетовая карта</option>
            <option value={1}>Кредитная карта</option>
            <option value={2}>Наличные</option>
            <option value={3}>Вклад / накопительный</option>
            <option value={4}>Прочее</option>
          </select>
        </label>
        <label style={{ flex: '1 1 220px' }}>Банк
          <select value={bankId} onChange={e => setBankId(Number(e.target.value))}>
            <option value={0}>Без привязки</option>
            {banks.map(b => <option key={b.id} value={b.id}>{b.name}</option>)}
          </select>
        </label>
      </div>

      <div className="form-row">
        <label>{isCredit ? 'Текущий долг, ₽' : 'Баланс, ₽'}
          <input inputMode="decimal" value={balance} onChange={e => setBalance(e.target.value)} required placeholder="0" />
        </label>
        {isCredit && (
          <label>Кредитный лимит, ₽
            <input inputMode="decimal" value={limit} onChange={e => setLimit(e.target.value)} placeholder="100000" />
          </label>
        )}
        {!isCredit && !isDeposit && (
          <label>Кэшбэк, %
            <input inputMode="decimal" value={cashback} onChange={e => setCashback(e.target.value)} placeholder="5" />
          </label>
        )}
        {isDeposit && (
          <label>Ставка, % годовых
            <input inputMode="decimal" value={rate} onChange={e => setRate(e.target.value)} placeholder="14" />
          </label>
        )}
        <label style={{ flex: '0 0 90px' }}>Цвет
          <input type="color" value={color} onChange={e => setColor(e.target.value)} />
        </label>
      </div>

      {isDeposit && (
        <div className="form-row">
          <label style={{ flex: '1 1 260px' }}>Начисление процентов
            <select value={interestMode} onChange={e => setInterestMode(Number(e.target.value))}>
              <option value={2}>Ежедневно на остаток, выплата раз в месяц</option>
              <option value={1}>Ежедневно на остаток, с капитализацией</option>
              <option value={3}>В конце срока (вклад)</option>
              <option value={0}>Без ставки</option>
            </select>
          </label>
          <label>Дата открытия
            <input type="date" value={openedAt} onChange={e => setOpenedAt(e.target.value)} />
          </label>
          {interestMode === 3 && (
            <label>Дата погашения
              <input type="date" value={maturesAt} onChange={e => setMaturesAt(e.target.value)} />
            </label>
          )}
          <label>НДФЛ, %
            <input inputMode="decimal" value={taxRate} onChange={e => setTaxRate(e.target.value)} placeholder="13" />
          </label>
        </div>
      )}

      <div className="muted" style={{ marginBottom: 12 }}>
        {isCredit
          ? 'По кредитной карте баланс = сумма долга; считается доступный лимит и процент использования.'
          : isDeposit
            ? 'Для вкладов и накопительных счетов задаётся ставка % годовых вместо кэшбэка. Приложение считает доход по формуле «остаток × ставка / 365» (Act/365) и показывает прогноз до и после налога.'
            : cashback ? 'Кэшбэк начисляется на расходы с этого счёта автоматически.' : 'Для наличных кэшбэк не нужен.'}
      </div>

      {err && <div className="alert alert-danger">{err}</div>}
      <div className="action-row">
        <button className="btn primary" type="submit" disabled={busy}>
          <Icon name="check" size={16} /> {busy ? 'Сохраняем…' : (isNew ? 'Создать счёт' : 'Сохранить')}
        </button>
        <button type="button" className="btn secondary" onClick={onClose}>Отмена</button>
      </div>
    </form>
  );
}

// ---- Семья: приглашения и состав группы ----
export function FamilyCard() {
  const [members, setMembers] = useState<FamilyMember[] | null>(null);
  const [familyCode, setFamilyCode] = useState<string | null>(null);
  const [inviteCode, setInviteCode] = useState<string | null>(null);
  const [expiresAt, setExpiresAt] = useState<string | null>(null);
  const [joinCode, setJoinCode] = useState('');
  const [msg, setMsg] = useState('');
  const [err, setErr] = useState('');

  const load = () => {
    api.get<{ familyCode: string | null; members: FamilyMember[] }>('/api/family/members')
      .then(r => { setFamilyCode(r.familyCode); setMembers(r.members); })
      .catch(e => setErr(e.message));
    api.get<{ code: string | null; expiresAt: string | null }>('/api/family/status')
      .then(s => { if (s.code) { setInviteCode(s.code); setExpiresAt(s.expiresAt); } })
      .catch(() => {});
  };
  useEffect(load, []);

  const invite = async () => {
    setErr(''); setMsg('');
    try {
      const r = await api.post<{ code: string; expiresAt: string }>('/api/family/invite');
      setInviteCode(r.code); setExpiresAt(r.expiresAt);
      load();
    } catch (ex: any) { setErr(ex.message); }
  };
  const join = async (e: React.FormEvent) => {
    e.preventDefault(); setErr(''); setMsg('');
    try {
      const r = await api.post<{ familyCode: string; members: FamilyMember[] }>('/api/family/join', { code: joinCode.trim().toUpperCase() });
      setMsg(`Добро пожаловать в семью! Участников: ${r.members.length}`);
      setJoinCode(''); load();
    } catch (ex: any) { setErr(ex.message); }
  };
  const leave = async () => {
    if (!confirm('Выйти из семьи?')) return;
    try { await api.post('/api/family/leave'); setInviteCode(null); load(); }
    catch (ex: any) { setErr(ex.message); }
  };
  const copy = () => {
    if (inviteCode) navigator.clipboard?.writeText(inviteCode).then(() => setMsg('Код скопирован')).catch(() => {});
  };

  const iAmOwner = !!members && members.some(m => m.isCreator);
  const showJoinForm = !iAmOwner;

  return (
    <div className="card">
      <div className="card-head">
        <div>
          <h3>Семья</h3>
          <p className="card-sub">Общий доступ к счетам близких</p>
        </div>
        <span className="badge badge-muted"><Icon name="user" size={12} /> {members?.length ?? 0}</span>
      </div>

      {err && <div className="alert alert-danger">{err}</div>}

      {!members ? (
        <div className="muted">Загрузка…</div>
      ) : members.length > 0 ? (
        <>
          <div className="family-list">
            {members.map(m => (
              <div className="family-member" key={m.id}>
                <span className="member-avatar">{accountInitial(m.name)}</span>
                <div className="member-info">
                  <b>{m.name}{m.isOwner ? ' · создатель' : ''}</b>
                  <span>{m.email}</span>
                </div>
                {m.isOwner && <span className="badge badge-muted">owner</span>}
              </div>
            ))}
          </div>

          <div className="muted" style={{ marginBottom: 10 }}>
            Код семьи: <b style={{ color: 'var(--text-2)' }}>{familyCode}</b>
          </div>

          {inviteCode && (
            <div className="invite-code">
              <div>
                <div className="muted" style={{ fontSize: 11 }}>Пригласительный код</div>
                <b>{inviteCode}</b>
              </div>
              <button className="btn small" onClick={copy} type="button">
                <Icon name="check" size={13} /> Копировать
              </button>
            </div>
          )}

          <div className="action-row" style={{ marginTop: 8 }}>
            <button className="btn primary small" onClick={invite} type="button">
              <Icon name="sparkles" size={14} /> Создать код
            </button>
            <button className="btn danger-ghost small" onClick={leave} type="button">
              <Icon name="logout" size={14} /> Выйти из семьи
            </button>
          </div>
        </>
      ) : (
        <>
          <div className="muted" style={{ marginBottom: 12 }}>
            Вы пока не в семье. Создайте семью и отправьте близким пригласительный код —
            либо введите код, который получили от них.
          </div>
          <button className="btn primary" onClick={invite} type="button">
            <Icon name="sparkles" size={16} /> Создать семью и получить код
          </button>
          {inviteCode && (
            <div className="invite-code" style={{ marginTop: 12, marginBottom: 0 }}>
              <div>
                <div className="muted" style={{ fontSize: 11 }}>Передайте близким</div>
                <b>{inviteCode}</b>
                {expiresAt && <div className="muted" style={{ fontSize: 11 }}>до {new Date(expiresAt).toLocaleDateString('ru-RU')}</div>}
              </div>
              <button className="btn small" onClick={copy} type="button">Копировать</button>
            </div>
          )}
        </>
      )}

      {showJoinForm && (
        <form onSubmit={join} style={{ marginTop: 14 }}>
          <label>У меня есть код
            <input
              value={joinCode}
              onChange={e => setJoinCode(e.target.value)}
              placeholder="напр. K7M2QX"
              maxLength={6}
              style={{ textTransform: 'uppercase', letterSpacing: 2 }}
            />
          </label>
          <button className="btn secondary" type="submit" disabled={!joinCode.trim()}>
            <Icon name="arrow-up-right" size={15} /> Присоединиться
          </button>
        </form>
      )}

      {iAmOwner && (
        <div className="muted" style={{ marginTop: 10 }}>
          Вы создали эту семью — приглашайте близких кнопкой выше.
        </div>
      )}
      {msg && <div className="alert alert-info" style={{ marginBottom: 0 }}>{msg}</div>}
    </div>
  );
}
