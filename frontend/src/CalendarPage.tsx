import { useEffect, useMemo, useState } from 'react';
import {
  api, fmt, AccountInfo, CalendarItem, DebtInfo, ObligationInfo,
} from './api';
import { Icon } from './icons';
import { useDialog } from './dialogs';
import { ScopeSwitch } from './ScopeSwitch';
import { loadScope, withScope, SCOPE_CHANGED, type ScopeState } from './scope';

type View = 'month' | 'week' | 'upcoming';

const VIEWS: { v: View; label: string }[] = [
  { v: 'month', label: 'Месяц' },
  { v: 'week', label: 'Неделя' },
  { v: 'upcoming', label: 'Ближайшие' },
];

const SOURCES = [
  { v: 0, label: 'События', color: '#805ad5' },
  { v: 1, label: 'Платежи', color: '#dd6b20' },
  { v: 2, label: 'Сроки долгов', color: '#e53e3e' },
  { v: 3, label: 'Вклады', color: '#2b6cb0' },
  { v: 4, label: 'Проценты', color: '#38a169' },
];

const TYPES = [
  { v: 0, label: 'Событие' },
  { v: 1, label: 'Напоминание' },
  { v: 2, label: 'Встреча' },
  { v: 3, label: 'Прочее' },
];

const num = (s: string) => Number(s.replace(',', '.')) || 0;
const iso = (d: Date) =>
  `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
const parseIso = (s: string) => new Date(s + 'T00:00:00');
const addDays = (d: Date, n: number) => {
  const x = new Date(d); x.setDate(x.getDate() + n); return x;
};
const startOfWeek = (d: Date) => addDays(d, -((d.getDay() + 6) % 7));
const todayIso = () => iso(new Date());
const colorOf = (source: number) => SOURCES.find(s => s.v === source)?.color ?? '#718096';

/**
 * Календарь событий (Задача 5): единое представление дат по платежам, долгам,
 * вкладам, начислениям процентов и пользовательским событиям.
 */
export function CalendarPage() {
  const dlg = useDialog();
  const [view, setView] = useState<View>('month');
  const [anchor, setAnchor] = useState(new Date());
  const [items, setItems] = useState<CalendarItem[]>([]);
  const [filter, setFilter] = useState<number[]>([]);
  const [scope, setScope] = useState<ScopeState>(loadScope);
  const [debts, setDebts] = useState<DebtInfo[]>([]);
  const [obligations, setObligations] = useState<ObligationInfo[]>([]);
  const [accounts, setAccounts] = useState<AccountInfo[]>([]);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);

  const [formOpen, setFormOpen] = useState(false);
  const [editing, setEditing] = useState<CalendarItem | null>(null);
  const [fTitle, setFTitle] = useState('');
  const [fDate, setFDate] = useState(todayIso());
  const [fTime, setFTime] = useState('');
  const [fType, setFType] = useState(0);
  const [fAmount, setFAmount] = useState('');
  const [fRemind, setFRemind] = useState(true);
  const [fNote, setFNote] = useState('');
  const [fDebtId, setFDebtId] = useState('');
  const [fObligationId, setFObligationId] = useState('');
  const [fAccountId, setFAccountId] = useState('');

  const range = useMemo(() => {
    const from = view === 'upcoming' ? new Date(anchor) : startOfWeek(
      view === 'month' ? new Date(anchor.getFullYear(), anchor.getMonth(), 1) : anchor);
    const days = view === 'month' ? 42 : view === 'upcoming' ? 31 : 7;
    return { from: iso(from), to: iso(addDays(from, days - 1)), days: Array.from({ length: days }, (_, i) => addDays(from, i)) };
  }, [view, anchor]);

  const load = async () => {
    try {
      const q = filter.length ? `&types=${filter.join(',')}` : '';
      const [list, d, o, a] = await Promise.all([
        api.get<CalendarItem[]>(withScope(`/api/calendar?from=${range.from}&to=${range.to}${q}`, scope)),
        api.get<DebtInfo[]>(withScope('/api/debts?filter=active', scope)),
        api.get<ObligationInfo[]>(withScope('/api/obligations?filter=active', scope)),
        api.get<AccountInfo[]>('/api/accounts'),
      ]);
      setItems(list);
      setDebts(d); setObligations(o); setAccounts(a.filter(x => !x.isArchived));
    } catch (ex: any) { setErr(ex.message); }
  };

  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [view, anchor, filter, scope]);
  useEffect(() => {
    const sync = () => setScope(loadScope());
    window.addEventListener(SCOPE_CHANGED, sync);
    return () => window.removeEventListener(SCOPE_CHANGED, sync);
  }, []);

  const byDate = useMemo(() => {
    const map = new Map<string, CalendarItem[]>();
    for (const item of items) {
      const arr = map.get(item.date) ?? [];
      arr.push(item); map.set(item.date, arr);
    }
    return map;
  }, [items]);

  const shift = (delta: number) => {
    const d = new Date(anchor);
    if (view === 'month') d.setMonth(d.getMonth() + delta);
    else d.setDate(d.getDate() + delta * (view === 'week' ? 7 : 30));
    setAnchor(d);
  };

  const resetForm = () => {
    setEditing(null);
    setFTitle(''); setFDate(todayIso()); setFTime(''); setFType(0);
    setFAmount(''); setFRemind(true); setFNote('');
    setFDebtId(''); setFObligationId(''); setFAccountId('');
  };

  const startCreate = (date?: string) => {
    resetForm();
    if (date) setFDate(date);
    setFormOpen(true);
  };

  const startEdit = (item: CalendarItem) => {
    setEditing(item);
    setFTitle(item.title); setFDate(item.date); setFTime(item.time ?? '');
    const idx = TYPES.findIndex(t => t.label === item.typeName);
    setFType(idx >= 0 ? idx : 0);
    setFAmount(item.amount ? String(item.amount) : '');
    setFRemind(item.remind); setFNote(item.note);
    setFDebtId(item.debtId ? String(item.debtId) : '');
    setFObligationId(item.obligationId ? String(item.obligationId) : '');
    setFAccountId(item.accountId ? String(item.accountId) : '');
    setFormOpen(true);
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setErr(''); setBusy(true);
    const body = {
      title: fTitle, date: fDate || null, time: fTime || null, type: fType,
      amount: fAmount ? num(fAmount) : null, remind: fRemind, note: fNote,
      debtId: fDebtId ? Number(fDebtId) : null,
      obligationId: fObligationId ? Number(fObligationId) : null,
      accountId: fAccountId ? Number(fAccountId) : null,
    };
    try {
      if (editing) await api.put(withScope(`/api/calendar/events/${editing.id}`, scope), body);
      else await api.post(withScope('/api/calendar/events', scope), body);
      setFormOpen(false); resetForm(); await load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const del = async (item: CalendarItem) => {
    if (!await dlg.confirm(`Удалить событие «${item.title}»?`)) return;
    try { await api.del(withScope(`/api/calendar/events/${item.id}`, scope)); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const toggleFilter = (v: number) =>
    setFilter(prev => prev.includes(v) ? prev.filter(x => x !== v) : [...prev, v]);

  const renderItem = (item: CalendarItem, compact = false) => (
    <div key={item.key} className="tx-item" style={{ padding: '4px 0' }}>
      <span style={{ width: 6, height: 6, borderRadius: 6, background: colorOf(item.source), flex: '0 0 6px' }} />
      <div className="tx-main">
        <div className="tx-title" style={{ fontSize: 12, color: item.isOverdue ? 'var(--red)' : undefined }}>
          {item.title}{item.time ? ` · ${item.time}` : ''}{item.remind ? ' 🔔' : ''}
        </div>
        {!compact && (
          <div className="tx-sub">
            {item.sourceName}{item.typeName ? ` · ${item.typeName}` : ''}
            {item.note ? ` · ${item.note}` : ''}
            {scope.mode !== 'me' && item.ownerName ? ` · 👤 ${item.ownerName}` : ''}
          </div>
        )}
      </div>
      {item.amount !== null && <b style={{ fontSize: 12 }}>{fmt(item.amount)}</b>}
      {item.source === 0 && (
        <>
          <button className="btn secondary" title="Изменить" onClick={() => startEdit(item)}>
            <Icon name="pencil" size={12} />
          </button>
          <button className="btn danger-ghost" title="Удалить" onClick={() => del(item)}>
            <Icon name="trash" size={12} />
          </button>
        </>
      )}
    </div>
  );

  const monthTitle = new Intl.DateTimeFormat('ru-RU', { month: 'long', year: 'numeric' }).format(anchor);
  const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1);

  return (
    <div className="page">
      <div className="card">
        <div className="card-head">
          <div>
            <h4 style={{ margin: 0 }}>Календарь событий</h4>
            <p className="card-sub">Все важные даты в одном месте — платежи, долги, вклады и напоминания</p>
          </div>
          <button className="btn primary" onClick={() => startCreate(todayIso())}>
            <Icon name="plus" size={15} /> Новое событие
          </button>
        </div>

        {err && <div className="alert alert-danger">{err}</div>}

        <div className="segmented" style={{ marginTop: 10, maxWidth: 360 }}>
          {VIEWS.map(v => (
            <button
              key={v.v} type="button" className={`segment ${view === v.v ? 'active' : ''}`}
              onClick={() => setView(v.v)}
            >{v.label}</button>
          ))}
        </div>

        <div style={{ marginTop: 10 }}><ScopeSwitch /></div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, flexWrap: 'wrap' }}>
          <button className="btn secondary" onClick={() => shift(-1)}>‹</button>
          <button className="btn secondary" onClick={() => setAnchor(new Date())}>Сегодня</button>
          <button className="btn secondary" onClick={() => shift(1)}>›</button>
          <strong style={{ marginLeft: 8 }}>
            {view === 'month' ? capitalize(monthTitle)
              : view === 'week' ? `${iso(range.days[0])} — ${iso(range.days[6])}`
              : 'Ближайшие 30 дней'}
          </strong>
        </div>

        <div style={{ display: 'flex', gap: 6, marginTop: 10, flexWrap: 'wrap' }}>
          {SOURCES.map(s => (
            <button
              key={s.v} type="button"
              className={`btn ${filter.length === 0 || filter.includes(s.v) ? '' : 'secondary'}`}
              style={{ fontSize: 12, borderColor: colorOf(s.v) }}
              onClick={() => toggleFilter(s.v)}
            >
              <span style={{ display: 'inline-block', width: 7, height: 7, borderRadius: 7, background: colorOf(s.v), marginRight: 6 }} />
              {s.label}
            </button>
          ))}
        </div>
      </div>

      {formOpen && (
        <div className="card">
          <form onSubmit={submit}>
            <div className="card-head">
              <div>
                <h4 style={{ margin: 0 }}>{editing ? 'Изменить событие' : 'Новое событие'}</h4>
                <p className="card-sub">Своя дата, напоминание и связь с финансами</p>
              </div>
            </div>
            <div className="form-row">
              <label style={{ flex: '2 1 220px' }}>Название
                <input value={fTitle} onChange={e => setFTitle(e.target.value)} required placeholder="Отдать документы" />
              </label>
              <label>Тип
                <select value={fType} onChange={e => setFType(Number(e.target.value))}>
                  {TYPES.map(t => <option key={t.v} value={t.v}>{t.label}</option>)}
                </select>
              </label>
              <label>Дата
                <input type="date" value={fDate} onChange={e => setFDate(e.target.value)} required />
              </label>
              <label>Время
                <input type="time" value={fTime} onChange={e => setFTime(e.target.value)} />
              </label>
              <label>Сумма
                <input inputMode="decimal" value={fAmount} onChange={e => setFAmount(e.target.value)} placeholder="необязательно" />
              </label>
              <label>Счёт
                <select value={fAccountId} onChange={e => setFAccountId(e.target.value)}>
                  <option value="">— нет —</option>
                  {accounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                </select>
              </label>
              <label>Долг
                <select value={fDebtId} onChange={e => setFDebtId(e.target.value)}>
                  <option value="">— нет —</option>
                  {debts.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}
                </select>
              </label>
              <label>Платёж
                <select value={fObligationId} onChange={e => setFObligationId(e.target.value)}>
                  <option value="">— нет —</option>
                  {obligations.map(o => <option key={o.id} value={o.id}>{o.name}</option>)}
                </select>
              </label>
              <label style={{ flex: '1 1 auto', justifyContent: 'flex-end' }}>
                <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <input type="checkbox" checked={fRemind} onChange={e => setFRemind(e.target.checked)} />
                  Напоминать
                </span>
              </label>
              <label style={{ flex: '2 1 240px' }}>Заметка
                <input value={fNote} onChange={e => setFNote(e.target.value)} />
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

      {view === 'month' && (
        <div className="card">
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', gap: 4 }}>
            {['Пн', 'Вт', 'Ср', 'Чт', 'Пт', 'Сб', 'Вс'].map(d => (
              <div key={d} className="muted" style={{ fontSize: 11, textAlign: 'center', padding: 4 }}>{d}</div>
            ))}
            {range.days.map(day => {
              const key = iso(day);
              const dayItems = byDate.get(key) ?? [];
              const outside = day.getMonth() !== anchor.getMonth();
              return (
                <div
                  key={key}
                  style={{
                    minHeight: 96, border: '1px solid var(--border)', borderRadius: 8, padding: 4,
                    background: outside ? 'var(--fill)' : undefined,
                    cursor: 'pointer',
                  }}
                  onDoubleClick={() => startCreate(key)}
                >
                  <div className="muted" style={{ fontSize: 11, marginBottom: 2 }}>{day.getDate()}</div>
                  {dayItems.slice(0, 3).map(i => renderItem(i, true))}
                  {dayItems.length > 3 && <div className="muted" style={{ fontSize: 10 }}>+{dayItems.length - 3}</div>}
                </div>
              );
            })}
          </div>
          <p className="muted" style={{ marginTop: 10, fontSize: 12 }}>
            Двойной клик по дню создаёт событие. Цвет точки — источник события.
          </p>
        </div>
      )}

      {view === 'week' && (
        <div className="card">
          {range.days.map(day => {
            const key = iso(day);
            const dayItems = byDate.get(key) ?? [];
            return (
              <div key={key} style={{ marginBottom: 12 }}>
                <div style={{ fontWeight: 600, fontSize: 13 }}>
                  {new Intl.DateTimeFormat('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' }).format(day)}
                </div>
                {dayItems.length === 0
                  ? <div className="muted" style={{ fontSize: 12 }}>Событий нет</div>
                  : dayItems.map(i => renderItem(i))}
              </div>
            );
          })}
        </div>
      )}

      {view === 'upcoming' && (
        <div className="card">
          {items.length === 0 && <p className="muted">Ближайших событий нет.</p>}
          {items.map(i => (
            <div key={i.key} style={{ marginBottom: 8 }}>
              <div className="muted" style={{ fontSize: 11 }}>
                {new Intl.DateTimeFormat('ru-RU', { day: 'numeric', month: 'long' }).format(parseIso(i.date))}
              </div>
              {renderItem(i, true)}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
