import { useEffect, useState } from 'react';
import {
  api, fmt, AccountInfo, Category, DebtInfo, ObligationInfo, ObligationsSummary,
} from './api';
import { Icon } from './icons';
import { useDialog } from './dialogs';
import { ScopeSwitch } from './ScopeSwitch';
import { loadScope, withScope, SCOPE_CHANGED, type ScopeState } from './scope';

const PERIODS = [
  { v: 0, label: 'Разово' },
  { v: 1, label: 'Ежемесячно' },
  { v: 2, label: 'Ежеквартально' },
  { v: 3, label: 'Ежегодно' },
  { v: 4, label: 'Произвольно (интервал в днях)' },
];

const FILTERS: { v: 'active' | 'paused' | 'completed' | 'all'; label: string }[] = [
  { v: 'active', label: 'Активные' },
  { v: 'paused', label: 'На паузе' },
  { v: 'completed', label: 'Завершённые' },
  { v: 'all', label: 'Все' },
];

const num = (s: string) => Number(s.replace(',', '.')) || 0;
const todayIso = () => new Date().toISOString().slice(0, 10);
const fmtDate = (iso: string | null | undefined) => iso
  ? new Date(iso + 'T00:00:00').toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', year: 'numeric' })
  : '—';
const monthName = () => new Intl.DateTimeFormat('ru-RU', { month: 'long' }).format(new Date());

/**
 * Обязательные платежи (Задача 3): план регулярных трат с периодичностью,
 * фактами оплаты и необязательной операцией по счёту.
 * Сам платёж в бюджет не входит — деньги появляются только через операцию.
 */
export function ObligationsPage() {
  const dlg = useDialog();
  const [items, setItems] = useState<ObligationInfo[]>([]);
  const [summary, setSummary] = useState<ObligationsSummary | null>(null);
  const [accounts, setAccounts] = useState<AccountInfo[]>([]);
  const [categories, setCategories] = useState<Category[]>([]);
  const [debts, setDebts] = useState<DebtInfo[]>([]);
  const [filter, setFilter] = useState<'active' | 'paused' | 'completed' | 'all'>('active');
  const [scope, setScope] = useState<ScopeState>(loadScope);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<ObligationInfo | null>(null);
  const [fName, setFName] = useState('');
  const [fPayee, setFPayee] = useState('');
  const [fCategoryId, setFCategoryId] = useState('');
  const [fAmount, setFAmount] = useState('');
  const [fPeriodicity, setFPeriodicity] = useState(1);
  const [fNextDate, setFNextDate] = useState(todayIso());
  const [fPaymentDay, setFPaymentDay] = useState('');
  const [fIntervalDays, setFIntervalDays] = useState('');
  const [fAccountId, setFAccountId] = useState('');
  const [fDebtId, setFDebtId] = useState('');
  const [fRemind, setFRemind] = useState(true);
  const [fNote, setFNote] = useState('');

  const [payFor, setPayFor] = useState<number | null>(null);
  const [pAmount, setPAmount] = useState('');
  const [pDate, setPDate] = useState(todayIso());
  const [pNote, setPNote] = useState('');
  const [pCreateTx, setPCreateTx] = useState(true);
  const [pAccountId, setPAccountId] = useState('');
  const [pReduceDebt, setPReduceDebt] = useState(false);

  const [expanded, setExpanded] = useState<number[]>([]);

  const load = async (nextFilter: typeof filter = filter) => {
    try {
      const [list, s, a, c, d] = await Promise.all([
        api.get<ObligationInfo[]>(withScope(`/api/obligations?filter=${nextFilter}`, scope)),
        api.get<ObligationsSummary>(withScope('/api/obligations/summary', scope)),
        api.get<AccountInfo[]>('/api/accounts'),
        api.get<Category[]>('/api/categories'),
        api.get<DebtInfo[]>(withScope('/api/debts?filter=active', scope)),
      ]);
      setItems(list);
      setSummary(s);
      setAccounts(a);
      setCategories(c.filter(x => x.type === 1));
      setDebts(d);
    } catch (ex: any) { setErr(ex.message); }
  };

  useEffect(() => { load(filter); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [filter, scope]);
  useEffect(() => {
    const sync = () => setScope(loadScope());
    window.addEventListener(SCOPE_CHANGED, sync);
    return () => window.removeEventListener(SCOPE_CHANGED, sync);
  }, []);

  const activeAccounts = accounts.filter(a => !a.isArchived);
  const periodUsesDay = fPeriodicity >= 1 && fPeriodicity <= 3;

  const resetForm = () => {
    setEditing(null);
    setFName(''); setFPayee(''); setFCategoryId(''); setFAmount('');
    setFPeriodicity(1); setFNextDate(todayIso()); setFPaymentDay('');
    setFIntervalDays(''); setFAccountId(''); setFDebtId(''); setFRemind(true); setFNote('');
  };

  const startCreate = () => {
    resetForm(); setFormOpen(true); setPayFor(null);
  };

  const startEdit = (o: ObligationInfo) => {
    setEditing(o);
    setFName(o.name); setFPayee(o.payee);
    setFCategoryId(o.categoryId ? String(o.categoryId) : '');
    setFAmount(String(o.amount));
    setFPeriodicity(o.periodicity);
    setFNextDate(o.nextPaymentDate);
    setFPaymentDay(o.paymentDay ? String(o.paymentDay) : '');
    setFIntervalDays(o.intervalDays ? String(o.intervalDays) : '');
    setFAccountId(o.accountId ? String(o.accountId) : '');
    setFDebtId(o.debtId ? String(o.debtId) : '');
    setFRemind(o.remind); setFNote(o.note);
    setFormOpen(true); setPayFor(null);
  };

  const submitObligation = async (e: React.FormEvent) => {
    e.preventDefault();
    setErr(''); setBusy(true);
    const body = {
      name: fName,
      payee: fPayee,
      categoryId: fCategoryId ? Number(fCategoryId) : null,
      amount: num(fAmount),
      periodicity: fPeriodicity,
      nextPaymentDate: fNextDate || null,
      paymentDay: periodUsesDay && fPaymentDay ? Number(fPaymentDay) : null,
      intervalDays: fPeriodicity === 4 ? num(fIntervalDays) : null,
      accountId: fAccountId ? Number(fAccountId) : null,
      remind: fRemind,
      debtId: fDebtId ? Number(fDebtId) : null,
      note: fNote,
    };
    try {
      if (editing) await api.put(withScope(`/api/obligations/${editing.id}`, scope), body);
      else await api.post(withScope('/api/obligations', scope), body);
      setFormOpen(false); resetForm();
      if (!editing && filter === 'completed') setFilter('active');
      else await load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const startPay = (o: ObligationInfo) => {
    setPayFor(o.id);
    setPAmount(String(o.amount));
    setPDate(todayIso()); setPNote(''); setPCreateTx(true);
    setPReduceDebt(o.debtId !== null);
    setPAccountId(o.accountId ? String(o.accountId) : (activeAccounts[0] ? String(activeAccounts[0].id) : ''));
    setFormOpen(false);
  };

  const submitPay = async (o: ObligationInfo, e: React.FormEvent) => {
    e.preventDefault();
    setErr(''); setBusy(true);
    try {
      await api.post(withScope(`/api/obligations/${o.id}/pay`, scope), {
        amount: pAmount ? num(pAmount) : null,
        date: pDate || null,
        note: pNote,
        createTransaction: pCreateTx,
        accountId: pAccountId ? Number(pAccountId) : null,
        reduceDebt: pReduceDebt,
      });
      setPayFor(null);
      if (filter === 'completed' || filter === 'paused') setFilter('active');
      else await load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const delPayment = async (o: ObligationInfo, paymentId: number) => {
    if (!await dlg.confirm('Удалить факт оплаты? Связанная операция тоже будет удалена.')) return;
    try { await api.del(withScope(`/api/obligations/${o.id}/payments/${paymentId}`, scope)); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const changeStatus = async (o: ObligationInfo, action: 'pause' | 'resume' | 'complete') => {
    try {
      await api.post(withScope(`/api/obligations/${o.id}/${action}`, scope));
      if (filter !== 'all') setFilter('all'); else await load();
    } catch (ex: any) { setErr(ex.message); }
  };

  const delObligation = async (id: number) => {
    if (!await dlg.confirm('Удалить платёж, историю оплат и связанные операции?')) return;
    try { await api.del(withScope(`/api/obligations/${id}`, scope)); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const toggleHistory = (id: number) =>
    setExpanded(prev => prev.includes(id) ? prev.filter(x => x !== id) : [...prev, id]);

  return (
    <div className="page">
      <div className="card">
        <div className="card-head">
          <div>
            <h4 style={{ margin: 0 }}>Обязательные платежи</h4>
            <p className="card-sub">Регулярные траты и сроки — отдельно от бюджета</p>
          </div>
          <button className="btn primary" onClick={startCreate}>
            <Icon name="plus" size={15} /> Новый платёж
          </button>
        </div>
        <div style={{ marginTop: 10 }}><ScopeSwitch /></div>

        {err && <div className="alert alert-danger">{err}</div>}

        {summary && (
          <div className="metric-row" style={{ marginTop: 10 }}>
            <div className="metric">
              <span>{fmt(summary.monthPlannedAmount)}</span>
              <small>за {monthName()} по графику</small>
            </div>
            <div className="metric">
              <span style={{ color: summary.overdueCount > 0 ? 'var(--red)' : undefined }}>
                {summary.overdueCount > 0 ? `${summary.overdueCount} · ${fmt(summary.overdueAmount)}` : 'нет'}
              </span>
              <small>просрочено</small>
            </div>
            <div className="metric">
              <span>{fmt(summary.paidThisMonthAmount)}</span>
              <small>оплачено за месяц</small>
            </div>
            <div className="metric">
              <span>{summary.activeCount}</span>
              <small>активных</small>
            </div>
            {summary.next && (
              <div className="metric">
                <span>{fmtDate(summary.next.nextPaymentDate)}</span>
                <small>ближайший · {summary.next.name}</small>
              </div>
            )}
          </div>
        )}

        <div className="segmented" style={{ marginTop: 12, maxWidth: 480 }}>
          {FILTERS.map(f => (
            <button
              key={f.v} type="button"
              className={`segment ${filter === f.v ? 'active' : ''}`}
              onClick={() => setFilter(f.v)}
            >
              {f.label}
            </button>
          ))}
        </div>

        <p className="muted" style={{ marginTop: 10, fontSize: 12 }}>
          Сам платёж — это план и в доходы/расходы не входит. Отметьте «создать операцию» при оплате,
          чтобы движение денег появилось в журнале и на счёте.
        </p>
      </div>

      {formOpen && (
        <div className="card">
          <form onSubmit={submitObligation}>
            <div className="card-head">
              <div>
                <h4 style={{ margin: 0 }}>{editing ? 'Изменить платёж' : 'Новый платёж'}</h4>
                <p className="card-sub">Что платим, кому, сколько и когда</p>
              </div>
            </div>
            <div className="form-row">
              <label style={{ flex: '2 1 220px' }}>Название
                <input value={fName} onChange={e => setFName(e.target.value)} required placeholder="Квартплата" />
              </label>
              <label>Получатель
                <input value={fPayee} onChange={e => setFPayee(e.target.value)} required placeholder="УК ЖКХ" />
              </label>
              <label>Сумма
                <input inputMode="decimal" value={fAmount} onChange={e => setFAmount(e.target.value)} required placeholder="3500" />
              </label>
              <label>Категория
                <select value={fCategoryId} onChange={e => setFCategoryId(e.target.value)}>
                  <option value="">— не выбрана —</option>
                  {categories.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </label>
              <label>Периодичность
                <select value={fPeriodicity} onChange={e => setFPeriodicity(Number(e.target.value))}>
                  {PERIODS.map(p => <option key={p.v} value={p.v}>{p.label}</option>)}
                </select>
              </label>
              <label>Дата следующего платежа
                <input type="date" value={fNextDate} onChange={e => setFNextDate(e.target.value)} />
              </label>
              {periodUsesDay && (
                <label>День платежа
                  <input
                    inputMode="numeric" value={fPaymentDay}
                    onChange={e => setFPaymentDay(e.target.value)} placeholder="10"
                  />
                </label>
              )}
              {fPeriodicity === 4 && (
                <label>Интервал, дней
                  <input
                    inputMode="numeric" value={fIntervalDays}
                    onChange={e => setFIntervalDays(e.target.value)} placeholder="14"
                  />
                </label>
              )}
              <label>Счёт списания
                <select value={fAccountId} onChange={e => setFAccountId(e.target.value)}>
                  <option value="">— не выбран —</option>
                  {activeAccounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                </select>
              </label>
              <label>Связанный долг
                <select value={fDebtId} onChange={e => setFDebtId(e.target.value)}>
                  <option value="">— нет —</option>
                  {debts.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
                </select>
              </label>
              <label style={{ flex: '1 1 auto', justifyContent: 'flex-end' }}>
                <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <input type="checkbox" checked={fRemind} onChange={e => setFRemind(e.target.checked)} />
                  Напоминать
                </span>
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

      {items.length === 0 && !formOpen && (
        <div className="card">
          <p className="muted" style={{ margin: 0 }}>
            {filter === 'active'
              ? 'Платежей нет. Нажмите «Новый платёж», чтобы запланировать регулярную трату.'
              : 'В этом списке платежей нет.'}
          </p>
        </div>
      )}

      {items.map(o => (
        <div className="card" key={o.id}>
          <div className="card-head">
            <div>
              <h4 style={{ margin: 0 }}>{o.name}</h4>
              <p className="card-sub">
                {o.payee} · {o.periodicityName}
                {o.categoryName ? ` · ${o.categoryName}` : ''}
                {o.remind ? ' · 🔔' : ''}
                {scope.mode !== 'me' && o.ownerName ? ` · 👤 ${o.ownerName}` : ''}
              </p>
            </div>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <span className={`badge ${o.isOverdue ? 'badge-danger' : o.status === 2 ? 'badge-muted' : o.status === 1 ? 'badge-warn' : 'badge-success'}`}>
                {o.statusName}
              </span>
              <button className="btn danger-ghost" title="Удалить платёж" onClick={() => delObligation(o.id)}>
                <Icon name="trash" size={15} />
              </button>
            </div>
          </div>

          <div className="metric-row" style={{ marginTop: 10 }}>
            <div className="metric"><span>{fmt(o.amount)}</span><small>сумма платежа</small></div>
            <div className="metric">
              <span style={{ color: o.isOverdue ? 'var(--red)' : undefined }}>
                {fmtDate(o.nextPaymentDate)}
              </span>
              <small>следующий платёж</small>
            </div>
            {o.status === 0 && (
              <div className="metric">
                <span style={{ color: o.daysLeft < 0 ? 'var(--red)' : undefined }}>
                  {o.daysLeft < 0 ? `просрочен на ${-o.daysLeft} дн.` : `${o.daysLeft} дн.`}
                </span>
                <small>до оплаты</small>
              </div>
            )}
            <div className="metric"><span>{o.accountName ?? '—'}</span><small>счёт</small></div>
            {o.debtName && (
              <div className="metric"><span>{o.debtName}</span><small>связанный долг</small></div>
            )}
          </div>

          {o.note && <p className="muted" style={{ marginTop: 8, fontSize: 12 }}>Заметка: {o.note}</p>}

          <div className="action-row">
            {o.status !== 2 && (
              <button className="btn" onClick={() => startPay(o)}>
                <Icon name="check" size={14} /> Оплатить
              </button>
            )}
            <button className="btn secondary" onClick={() => toggleHistory(o.id)}>
              <Icon name="operations" size={14} /> История ({o.paymentsCount})
            </button>
            <button className="btn secondary" onClick={() => startEdit(o)}>
              <Icon name="pencil" size={14} /> Изменить
            </button>
            {o.status === 0 && (
              <button className="btn secondary" onClick={() => changeStatus(o, 'pause')}>Пауза</button>
            )}
            {o.status === 1 && (
              <button className="btn secondary" onClick={() => changeStatus(o, 'resume')}>Возобновить</button>
            )}
            {o.status !== 2 && (
              <button className="btn secondary" onClick={() => changeStatus(o, 'complete')}>Завершить</button>
            )}
          </div>

          {payFor === o.id && (
            <form className="form-row" style={{ marginTop: 10 }} onSubmit={(e) => submitPay(o, e)}>
              <label>Сумма
                <input
                  inputMode="decimal" value={pAmount}
                  onChange={e => setPAmount(e.target.value)} required placeholder="1000"
                />
              </label>
              <label>Дата оплаты
                <input type="date" value={pDate} onChange={e => setPDate(e.target.value)} />
              </label>
              <label style={{ flex: '2 1 220px' }}>Комментарий
                <input value={pNote} onChange={e => setPNote(e.target.value)} placeholder="Оплачено" />
              </label>
              <label style={{ flex: '1 1 auto', justifyContent: 'flex-end' }}>
                <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <input
                    type="checkbox" checked={pCreateTx}
                    onChange={e => setPCreateTx(e.target.checked)}
                  />
                  Создать операцию
                </span>
              </label>
              {pCreateTx && (
                <label>Счёт
                  <select value={pAccountId} onChange={e => setPAccountId(e.target.value)}>
                    <option value="">— выберите —</option>
                    {activeAccounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                  </select>
                </label>
              )}
              {o.debtId && (
                <label style={{ flex: '2 1 260px', justifyContent: 'flex-end' }}>
                  <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <input
                      type="checkbox" checked={pReduceDebt}
                      onChange={e => setPReduceDebt(e.target.checked)}
                    />
                    Уменьшить остаток долга «{o.debtName}»
                  </span>
                </label>
              )}
              <button className="btn primary" type="submit" disabled={busy}>
                <Icon name="check" size={15} /> Оплатить
              </button>
              <button type="button" className="btn secondary" onClick={() => setPayFor(null)}>Отмена</button>
            </form>
          )}

          {expanded.includes(o.id) && (
            <div style={{ marginTop: 12 }}>
              {o.payments.length === 0 && <p className="muted">Оплат пока нет.</p>}
              {o.payments.map(p => (
                <div className="tx-item" key={p.id}>
                  <span className="tx-ico" style={{ width: 30, height: 30, flex: '0 0 30px' }}>
                    <Icon name="check" size={14} />
                  </span>
                  <div className="tx-main">
                    <div className="tx-title" style={{ fontSize: 13 }}>
                      Оплата{p.transactionId ? ' · операция создана' : ''}
                    </div>
                    <div className="tx-sub">
                      {fmtDate(p.date)}{p.note ? ` · ${p.note}` : ''}
                      {p.plannedDate !== p.date ? ` · по графику ${fmtDate(p.plannedDate)}` : ''}
                    </div>
                  </div>
                  <div className="holding-value"><b>{fmt(p.amount)}</b></div>
                  <button
                    className="btn danger-ghost"
                    title="Удалить факт оплаты"
                    onClick={() => delPayment(o, p.id)}
                  >
                    <Icon name="trash" size={14} />
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>
      ))}
    </div>
  );
}
