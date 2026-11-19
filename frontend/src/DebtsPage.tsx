import { useEffect, useState } from 'react';
import { api, fmt, AccountInfo, DebtInfo, DebtEventInfo, DebtsSummary } from './api';
import { Icon } from './icons';
import { useDialog } from './dialogs';
import { ScopeSwitch } from './ScopeSwitch';
import { loadScope, withScope, SCOPE_CHANGED, type ScopeState } from './scope';

const DIRECTIONS = [
  { v: 0, label: 'Я должен' },
  { v: 1, label: 'Мне должны' },
];

const EVENT_KINDS = [
  { v: 1, label: 'Возврат' },
  { v: 2, label: 'Начислены проценты' },
  { v: 3, label: 'Оплачены проценты' },
  { v: 4, label: 'Корректировка' },
  { v: 5, label: 'Списание' },
];

const FILTERS: { v: 'active' | 'closed' | 'all'; label: string }[] = [
  { v: 'active', label: 'Активные' },
  { v: 'closed', label: 'Закрытые' },
  { v: 'all', label: 'Все' },
];

const num = (s: string) => Number(s.replace(',', '.')) || 0;
const todayIso = () => new Date().toISOString().slice(0, 10);
const fmtDate = (iso: string | null | undefined) => iso
  ? new Date(iso + 'T00:00:00').toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' })
  : '—';

/** Влияние события на остаток — подпись в истории. */
const effectLabel = (e: DebtEventInfo) => {
  switch (e.kind) {
    case 0: return 'основа';
    case 2: return 'увеличил остаток';
    case 4: return e.amount >= 0 ? 'увеличила остаток' : 'уменьшила остаток';
    default: return 'уменьшило остаток';
  }
};

/**
 * Долги (Задача 2): обязательства «я должен» / «мне должны».
 * Учитываются отдельно от доходов и расходов бюджета; история событий
 * и необязательная операция по счёту при возврате.
 */
export function DebtsPage() {
  const dlg = useDialog();
  const [debts, setDebts] = useState<DebtInfo[]>([]);
  const [summary, setSummary] = useState<DebtsSummary | null>(null);
  const [accounts, setAccounts] = useState<AccountInfo[]>([]);
  const [filter, setFilter] = useState<'active' | 'closed' | 'all'>('active');
  const [scope, setScope] = useState<ScopeState>(loadScope);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<DebtInfo | null>(null);
  const [fDirection, setFDirection] = useState(0);
  const [fName, setFName] = useState('');
  const [fCounterparty, setFCounterparty] = useState('');
  const [fPrincipal, setFPrincipal] = useState('');
  const [fCurrency, setFCurrency] = useState('RUB');
  const [fIssuedAt, setFIssuedAt] = useState(todayIso());
  const [fDueDate, setFDueDate] = useState('');
  const [fRate, setFRate] = useState('');
  const [fTerms, setFTerms] = useState('');
  const [fAccountId, setFAccountId] = useState('');
  const [fNote, setFNote] = useState('');

  const [eventFor, setEventFor] = useState<number | null>(null);
  const [eKind, setEKind] = useState(1);
  const [eAmount, setEAmount] = useState('');
  const [eDate, setEDate] = useState(todayIso());
  const [eNote, setENote] = useState('');
  const [eCreateTx, setECreateTx] = useState(false);
  const [eAccountId, setEAccountId] = useState('');

  const [expanded, setExpanded] = useState<number[]>([]);

  const load = async (nextFilter: typeof filter = filter) => {
    try {
      const [d, s, a] = await Promise.all([
        api.get<DebtInfo[]>(withScope(`/api/debts?filter=${nextFilter}`, scope)),
        api.get<DebtsSummary>(withScope('/api/debts/summary', scope)),
        api.get<AccountInfo[]>('/api/accounts'),
      ]);
      setDebts(d);
      setSummary(s);
      setAccounts(a);
    } catch (ex: any) { setErr(ex.message); }
  };

  useEffect(() => { load(filter); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [filter, scope]);
  useEffect(() => {
    const sync = () => setScope(loadScope());
    window.addEventListener(SCOPE_CHANGED, sync);
    return () => window.removeEventListener(SCOPE_CHANGED, sync);
  }, []);

  const activeAccounts = accounts.filter(a => !a.isArchived);

  const resetForm = () => {
    setEditing(null);
    setFDirection(0);
    setFName(''); setFCounterparty(''); setFPrincipal('');
    setFCurrency('RUB'); setFIssuedAt(todayIso()); setFDueDate('');
    setFRate(''); setFTerms(''); setFAccountId(''); setFNote('');
  };

  const startCreate = () => {
    resetForm();
    setFormOpen(true);
    setEventFor(null);
  };

  const startEdit = (d: DebtInfo) => {
    setEditing(d);
    setFDirection(d.direction);
    setFName(d.name);
    setFCounterparty(d.counterparty);
    setFPrincipal(String(d.principalAmount));
    setFCurrency(d.currency);
    setFIssuedAt(d.issuedAt);
    setFDueDate(d.dueDate ?? '');
    setFRate(d.interestRate ? String(d.interestRate) : '');
    setFTerms(d.terms ?? '');
    setFAccountId(d.accountId ? String(d.accountId) : '');
    setFNote(d.note);
    setFormOpen(true);
    setEventFor(null);
  };

  const submitDebt = async (e: React.FormEvent) => {
    e.preventDefault();
    setErr(''); setBusy(true);
    const body = {
      direction: fDirection,
      counterparty: fCounterparty,
      name: fName,
      principalAmount: num(fPrincipal),
      currency: fCurrency,
      issuedAt: fIssuedAt || null,
      dueDate: fDueDate || null,
      interestRate: num(fRate),
      terms: fTerms || null,
      accountId: fAccountId ? Number(fAccountId) : null,
      note: fNote,
    };
    try {
      if (editing) await api.put(withScope(`/api/debts/${editing.id}`, scope), body);
      else await api.post(withScope('/api/debts', scope), body);
      setFormOpen(false);
      resetForm();
      if (!editing && filter === 'closed') setFilter('active');
      else await load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const startEvent = (d: DebtInfo) => {
    setEventFor(d.id);
    setEKind(1);
    setEAmount('');
    setEDate(todayIso());
    setENote('');
    setECreateTx(false);
    setEAccountId(d.accountId ? String(d.accountId) : (activeAccounts[0] ? String(activeAccounts[0].id) : ''));
    setFormOpen(false);
  };

  const submitEvent = async (d: DebtInfo, e: React.FormEvent) => {
    e.preventDefault();
    setErr(''); setBusy(true);
    try {
      await api.post(withScope(`/api/debts/${d.id}/events`, scope), {
        kind: eKind,
        amount: num(eAmount),
        date: eDate || null,
        note: eNote,
        createTransaction: eCreateTx,
        accountId: eAccountId ? Number(eAccountId) : null,
      });
      setEventFor(null);
      await load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const delEvent = async (debtId: number, eventId: number) => {
    if (!await dlg.confirm('Удалить событие? Связанная операция тоже будет удалена.')) return;
    try { await api.del(withScope(`/api/debts/${debtId}/events/${eventId}`, scope)); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const setStatus = async (d: DebtInfo, close: boolean) => {
    try {
      await api.post(withScope(`/api/debts/${d.id}/${close ? 'close' : 'reopen'}`, scope));
      await load();
    } catch (ex: any) { setErr(ex.message); }
  };

  const delDebt = async (id: number) => {
    if (!await dlg.confirm('Удалить долг, историю и связанные операции?')) return;
    try { await api.del(withScope(`/api/debts/${id}`, scope)); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const toggleHistory = (id: number) =>
    setExpanded(prev => prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id]);

  const txAllowed = eKind === 1 || eKind === 3;

  return (
    <div className="page">
      <div className="card">
        <div className="card-head">
          <div>
            <h4 style={{ margin: 0 }}>Долги</h4>
            <p className="card-sub">Кому должны вы и кто должен вам — отдельно от бюджета</p>
          </div>
          <button className="btn primary" onClick={startCreate}>
            <Icon name="plus" size={15} /> Новый долг
          </button>
        </div>
        <div style={{ marginTop: 10 }}><ScopeSwitch /></div>

        {err && <div className="alert alert-danger">{err}</div>}

        {summary && (
          <div className="metric-row" style={{ marginTop: 10 }}>
            <div className="metric">
              <span style={{ color: summary.iOwe > 0 ? 'var(--red)' : undefined }}>{fmt(summary.iOwe)}</span>
              <small>я должен</small>
            </div>
            <div className="metric">
              <span style={{ color: summary.owedToMe > 0 ? 'var(--green)' : undefined }}>{fmt(summary.owedToMe)}</span>
              <small>мне должны</small>
            </div>
            <div className="metric">
              <span style={{ color: summary.net >= 0 ? 'var(--green)' : 'var(--red)' }}>
                {summary.net >= 0 ? '+' : ''}{fmt(summary.net)}
              </span>
              <small>сальдо</small>
            </div>
            <div className="metric">
              <span style={{ color: summary.overdueCount > 0 ? 'var(--red)' : undefined }}>
                {summary.overdueCount > 0 ? `${summary.overdueCount} · ${fmt(summary.overdueAmount)}` : 'нет'}
              </span>
              <small>просрочено</small>
            </div>
            {summary.nearest && (
              <div className="metric">
                <span>{fmtDate(summary.nearest.dueDate)}</span>
                <small>ближайший срок · {summary.nearest.name}</small>
              </div>
            )}
          </div>
        )}

        <div className="segmented" style={{ marginTop: 12, maxWidth: 340 }}>
          {FILTERS.map(f => (
            <button
              key={f.v}
              type="button"
              className={`segment ${filter === f.v ? 'active' : ''}`}
              onClick={() => setFilter(f.v)}
            >
              {f.label}
            </button>
          ))}
        </div>

        <p className="muted" style={{ marginTop: 10, fontSize: 12 }}>
          Долги не входят в доходы и расходы. Если при возврате отметить «создать операцию»,
          движение денег появится в журнале и на счёте.
        </p>
      </div>

      {formOpen && (
        <div className="card">
          <form onSubmit={submitDebt}>
            <div className="card-head">
              <div>
                <h4 style={{ margin: 0 }}>{editing ? 'Изменить долг' : 'Новый долг'}</h4>
                <p className="card-sub">Направление, контрагент, сумма и условия</p>
              </div>
            </div>
            <div className="form-row">
              <label>Направление
                <select value={fDirection} onChange={e => setFDirection(Number(e.target.value))}>
                  {DIRECTIONS.map(d => <option key={d.v} value={d.v}>{d.label}</option>)}
                </select>
              </label>
              <label style={{ flex: '2 1 220px' }}>Название
                <input value={fName} onChange={e => setFName(e.target.value)} required placeholder="Займ на ремонт" />
              </label>
              <label>Контрагент
                <input value={fCounterparty} onChange={e => setFCounterparty(e.target.value)} required placeholder="Иван / банк" />
              </label>
              <label>Сумма
                <input inputMode="decimal" value={fPrincipal} onChange={e => setFPrincipal(e.target.value)} required placeholder="10000" />
              </label>
              <label>Валюта
                <input value={fCurrency} onChange={e => setFCurrency(e.target.value)} placeholder="RUB" />
              </label>
              <label>Дата возникновения
                <input type="date" value={fIssuedAt} onChange={e => setFIssuedAt(e.target.value)} />
              </label>
              <label>Срок возврата
                <input type="date" value={fDueDate} onChange={e => setFDueDate(e.target.value)} />
              </label>
              <label>Ставка, %
                <input inputMode="decimal" value={fRate} onChange={e => setFRate(e.target.value)} placeholder="0" />
              </label>
              <label>Счёт по умолчанию
                <select value={fAccountId} onChange={e => setFAccountId(e.target.value)}>
                  <option value="">— не выбран —</option>
                  {activeAccounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                </select>
              </label>
              <label style={{ flex: '2 1 240px' }}>Условия
                <input value={fTerms} onChange={e => setFTerms(e.target.value)} placeholder="Без процентов, вернуть до…" />
              </label>
              <label style={{ flex: '2 1 240px' }}>Заметка
                <input value={fNote} onChange={e => setFNote(e.target.value)} placeholder="Комментарий" />
              </label>
            </div>
            <div className="action-row">
              <button className="btn primary" type="submit" disabled={busy}>
                <Icon name="check" size={16} /> {editing ? 'Сохранить' : 'Создать'}
              </button>
              <button type="button" className="btn secondary" onClick={() => { setFormOpen(false); resetForm(); }}>
                Отмена
              </button>
            </div>
          </form>
        </div>
      )}

      {debts.length === 0 && !formOpen && (
        <div className="card">
          <p className="muted" style={{ margin: 0 }}>
            {filter === 'closed'
              ? 'Закрытых долгов нет.'
              : 'Долгов нет. Нажмите «Новый долг», чтобы записать, кому и сколько вы должны или кто должен вам.'}
          </p>
        </div>
      )}

      {debts.map(d => (
        <div className="card" key={d.id}>
          <div className="card-head">
            <div>
              <h4 style={{ margin: 0 }}>{d.name}</h4>
              <p className="card-sub">
                <span style={{ color: d.direction === 0 ? 'var(--red)' : 'var(--green)', fontWeight: 600 }}>
                  {d.directionName}
                </span>
                {' '}· {d.counterparty} · от {fmtDate(d.issuedAt)}
                {d.dueDate ? ` · до ${fmtDate(d.dueDate)}` : ''}
                {scope.mode !== 'me' && d.ownerName ? ` · 👤 ${d.ownerName}` : ''}
              </p>
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span className={`badge ${d.isOverdue ? 'badge-danger' : d.status === 1 ? 'badge-muted' : 'badge-success'}`}>
                {d.statusName}
              </span>
              <button className="btn danger-ghost" title="Удалить долг" onClick={() => delDebt(d.id)}>
                <Icon name="trash" size={15} />
              </button>
            </div>
          </div>

          <div className="metric-row" style={{ marginTop: 10 }}>
            <div className="metric">
              <span>{fmt(d.remainingAmount)}</span><small>остаток</small>
            </div>
            <div className="metric">
              <span>{fmt(d.principalAmount)}</span><small>основная сумма</small>
            </div>
            {d.interestRate > 0 && (
              <div className="metric"><span>{d.interestRate} %</span><small>ставка</small></div>
            )}
            <div className="metric">
              <span>{d.accountName ?? '—'}</span><small>счёт</small>
            </div>
            {d.dueDate && d.daysLeft !== null && d.remainingAmount > 0 && (
              <div className="metric">
                <span style={{ color: d.daysLeft < 0 ? 'var(--red)' : undefined }}>
                  {d.daysLeft < 0 ? `просрочен на ${-d.daysLeft} дн.` : `${d.daysLeft} дн.`}
                </span>
                <small>до срока</small>
              </div>
            )}
          </div>

          {d.terms && <p className="muted" style={{ marginTop: 8, fontSize: 12 }}>Условия: {d.terms}</p>}
          {d.note && <p className="muted" style={{ marginTop: 4, fontSize: 12 }}>Заметка: {d.note}</p>}

          {/* Задача 4: предстоящие платежи по связанному графику (суммы не добавляются в аналитику). */}
          {d.linkedObligations.length > 0 && (
            <div style={{ marginTop: 10 }}>
              <div className="muted" style={{ fontSize: 12, marginBottom: 4 }}>Связанные платежи</div>
              {d.linkedObligations.map(o => (
                <div className="tx-item" key={o.id}>
                  <span className="tx-ico" style={{ width: 30, height: 30, flex: '0 0 30px' }}>
                    <Icon name="calendar" size={14} />
                  </span>
                  <div className="tx-main">
                    <div className="tx-title" style={{ fontSize: 13 }}>{o.name}</div>
                    <div className="tx-sub">
                      {fmtDate(o.nextPaymentDate)} · {o.statusName}
                    </div>
                  </div>
                  <div className="holding-value">
                    <b style={{ color: o.isOverdue ? 'var(--red)' : undefined }}>{fmt(o.amount)}</b>
                  </div>
                </div>
              ))}
            </div>
          )}

          <div className="action-row">
            {d.remainingAmount > 0 && (
              <button className="btn" onClick={() => startEvent(d)}>
                <Icon name="operations" size={14} /> Вернуть / событие
              </button>
            )}
            <button className="btn secondary" onClick={() => toggleHistory(d.id)}>
              <Icon name="operations" size={14} /> История ({d.events.length})
            </button>
            <button className="btn secondary" onClick={() => startEdit(d)}>
              <Icon name="pencil" size={14} /> Изменить
            </button>
            {d.status === 1 ? (
              <button
                className="btn secondary"
                onClick={() => setStatus(d, false)}
                disabled={d.remainingAmount <= 0}
                title={d.remainingAmount <= 0 ? 'Остаток нулевой' : 'Снова открыть долг'}
              >
                Открыть
              </button>
            ) : (
              <button className="btn secondary" onClick={() => setStatus(d, true)}>Закрыть</button>
            )}
          </div>

          {eventFor === d.id && (
            <form className="form-row" style={{ marginTop: 10 }} onSubmit={(e) => submitEvent(d, e)}>
              <label>Событие
                <select value={eKind} onChange={e => setEKind(Number(e.target.value))}>
                  {EVENT_KINDS.map(k => <option key={k.v} value={k.v}>{k.label}</option>)}
                </select>
              </label>
              <label>Сумма
                <input
                  inputMode="decimal"
                  value={eAmount}
                  onChange={e => setEAmount(e.target.value)}
                  required
                  placeholder={eKind === 4 ? '−100 или 100' : '1000'}
                />
              </label>
              <label>Дата
                <input type="date" value={eDate} onChange={e => setEDate(e.target.value)} />
              </label>
              <label style={{ flex: '2 1 220px' }}>Комментарий
                <input value={eNote} onChange={e => setENote(e.target.value)} placeholder="Частичный возврат" />
              </label>
              {txAllowed && (
                <label style={{ flex: '1 1 auto', justifyContent: 'flex-end' }}>
                  <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <input
                      type="checkbox"
                      checked={eCreateTx}
                      onChange={e => setECreateTx(e.target.checked)}
                    />
                    Создать операцию
                  </span>
                </label>
              )}
              {txAllowed && eCreateTx && (
                <label>Счёт
                  <select value={eAccountId} onChange={e => setEAccountId(e.target.value)}>
                    <option value="">— выберите —</option>
                    {activeAccounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                  </select>
                </label>
              )}
              <button className="btn primary" type="submit" disabled={busy}>
                <Icon name="check" size={15} /> Добавить
              </button>
              <button type="button" className="btn secondary" onClick={() => setEventFor(null)}>Отмена</button>
            </form>
          )}

          {expanded.includes(d.id) && (
            <div style={{ marginTop: 12 }}>
              {d.events.length === 0 && <p className="muted">Событий пока нет.</p>}
              {d.events.map(ev => (
                <div className="tx-item" key={ev.id}>
                  <span className="tx-ico" style={{ width: 30, height: 30, flex: '0 0 30px' }}>
                    <Icon name={ev.kind === 2 || (ev.kind === 4 && ev.amount > 0) ? 'trending-up' : 'trending-down'} size={14} />
                  </span>
                  <div className="tx-main">
                    <div className="tx-title" style={{ fontSize: 13 }}>
                      {ev.kindName}
                      {ev.transactionId ? ' · операция создана' : ''}
                    </div>
                    <div className="tx-sub">
                      {fmtDate(ev.date)}{ev.note ? ` · ${ev.note}` : ''}
                    </div>
                  </div>
                  <div className="holding-value">
                    <b>{ev.amount < 0 ? '−' : ''}{fmt(Math.abs(ev.amount))}</b>
                    <span className="muted" style={{ fontSize: 11 }}>{effectLabel(ev)}</span>
                  </div>
                  {ev.kind !== 0 && (
                    <button
                      className="btn danger-ghost"
                      title="Удалить событие"
                      onClick={() => delEvent(d.id, ev.id)}
                    >
                      <Icon name="trash" size={14} />
                    </button>
                  )}
                </div>
              ))}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
