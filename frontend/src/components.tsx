import { useEffect, useState } from 'react';
import { Summary, Category, Tx, TxType, AccountInfo, api, fmt, MONTHS_RU } from './api';

// ---- Дашборд: обзор бюджета + анализ + рекомендации ----
export function Dashboard() {
  const [sum, setSum] = useState<Summary | null>(null);
  const [recent, setRecent] = useState<Tx[]>([]);
  const [err, setErr] = useState('');

  const reload = () => {
    const now = new Date();
    Promise.all([
      api.get<Summary>('/api/budget/summary'),
      api.get<Tx[]>(`/api/transactions?month=${now.getMonth() + 1}&year=${now.getFullYear()}&take=8`),
    ]).then(([s, t]) => { setSum(s); setRecent(t); }).catch(e => setErr(e.message));
  };
  useEffect(reload, []);

  if (err) return <div className="alert">{err}</div>;
  if (!sum) return <div className="muted card">Загрузка…</div>;

  const months = sum.byMonth.slice(-6);
  const maxVal = Math.max(...months.flatMap(m => [m.income, m.expense]), 1);
  const totalExpense = sum.expenseShare.reduce((s, x) => s + x.total, 0);

  return (
    <>
      <div className="metric-box">
        <div className="metric"><span>{fmt(sum.monthIncome)}</span><small>Доход за месяц</small></div>
        <div className="metric"><span>{fmt(sum.monthExpense)}</span><small>Расход за месяц</small></div>
        <div className={`metric ${sum.balance >= 0 ? 'pos' : 'neg'}`}>
          <span>{fmt(sum.balance)}</span><small>Итог месяца</small></div>
        <div className={`metric ${sum.totalBalance >= 0 ? 'pos' : 'neg'}`}>
          <span>{fmt(sum.totalBalance)}</span><small>Баланс с учётом счетов</small></div>
      </div>

      <div className="card">
        <h3>💡 Персональные рекомендации</h3>
        {sum.recommendations.map((r, i) => (
          <div key={i} className={`rec rec-${r.level}`}>
            <span className="rec-icon">{r.icon}</span>
            <span>{r.text}</span>
          </div>
        ))}
      </div>

      <div className="card">
        <h3>📈 Динамика (6 мес.) · норма сбережений {sum.savingsRate}%</h3>
        {months.length === 0 ? <div className="muted">Пока нет данных</div> : (
          <div className="chart">
            {months.map(m => (
              <div className="col" key={`${m.year}-${m.month}`}>
                <div className="bars">
                  <div className="bar income" style={{ height: `${(m.income / maxVal) * 90}px` }} title={fmt(m.income)} />
                  <div className="bar expense" style={{ height: `${(m.expense / maxVal) * 90}px` }} title={fmt(m.expense)} />
                </div>
                <div className="lbl">{MONTHS_RU[m.month - 1].slice(0, 3)}</div>
              </div>
            ))}
          </div>
        )}
      </div>

      <div className="card">
        <h3>🥧 Структура расходов ({MONTHS_RU[sum.month - 1]})</h3>
        {sum.expenseShare.length === 0 ? <div className="muted">В этом месяце расходов пока нет</div> : (
          <div className="stack-bar-wrap">
            <div className="stack-bar">
              {sum.expenseShare.map(s => (
                <div key={s.categoryId} style={{ width: `${(s.total / totalExpense) * 100}%`, background: s.color }}
                  title={`${s.name}: ${fmt(s.total)}`} />
              ))}
            </div>
            <div className="legend">
              {sum.expenseShare.map(s => (
                <div className="legend-item" key={s.categoryId}>
                  <b style={{ color: s.color }}>●</b> {s.name}
                  <span className="muted"> {fmt(s.total)} · {Math.round((s.total / totalExpense) * 100)}%</span>
                </div>
              ))}
            </div>
          </div>
        )}
      </div>

      <div className="card">
        <h3>📊 Лимиты категорий ({MONTHS_RU[sum.month - 1]})</h3>
        {sum.limits.length === 0 &&
          <div className="muted">Лимиты не заданы — настройте в разделе «Цели» → Лимиты</div>}
        {sum.limits.map(l => {
          const pct = Math.min(100, (l.spent / l.limit) * 100);
          const over = l.spent > l.limit;
          return (
            <div className="limit-row" key={l.id}>
              <div className="limit-head">
                <span><b style={{ color: l.color }}>●</b> {l.name}</span>
                <span className={over ? 'badge badge-danger' : 'muted'}>{fmt(l.spent)} / {fmt(l.limit)}</span>
              </div>
              <div className="progress"><div className={over ? 'over' : ''} style={{ width: `${pct}%`, background: over ? undefined : l.color }} /></div>
            </div>
          );
        })}
      </div>

      {sum.goals.length > 0 && (
        <div className="card">
          <h3>🎯 Финансовые цели</h3>
          {sum.goals.map(g => (
            <div className="limit-row" key={g.id}>
              <div className="limit-head">
                <span><b>{g.name}</b></span>
                <span className="muted">{fmt(g.currentAmount)} / {fmt(g.targetAmount)}</span>
              </div>
              <div className="progress"><div style={{ width: `${Math.min(100, g.progress)}%`, background: 'var(--success)' }} /></div>
              {g.perMonth !== null && g.monthsLeft !== null && (
                <div className="muted" style={{ marginTop: 2 }}>
                  Чтобы успеть к сроку: {fmt(g.perMonth)}/мес (осталось мес.: {g.monthsLeft})
                </div>
              )}
            </div>
          ))}
        </div>
      )}

      <div className="card">
        <h3>🧾 Последние операции</h3>
        {recent.length === 0 ? <div className="muted">В этом месяце операций нет. Нажмите ＋ внизу справа.</div> :
          recent.map(t => <TxRow key={t.id} tx={t} />)}
      </div>
    </>
  );
}

export function TxRow({ tx, onDelete }: { tx: Tx; onDelete?: (id: number) => void }) {
  const income = tx.type === 0;
  return (
    <div className="tx-item">
      <div className="tx-dot" style={{ background: tx.categoryColor || '#718096' }} />
      <div className="tx-main">
        <div className="t">{tx.categoryName}{tx.comment ? ` — ${tx.comment}` : ''}</div>
        <div className="s">{new Date(tx.date).toLocaleDateString('ru-RU')}{tx.accountName ? ` · ${tx.accountName}` : ''}</div>
      </div>
      <div className={`tx-sum ${income ? 'income' : 'expense'}`}>{income ? '+' : '−'}{fmt(tx.amount)}</div>
      {onDelete && <button className="btn danger-ghost small" onClick={() => onDelete(tx.id)}>✕</button>}
    </div>
  );
}

// ---- Быстрая операция (FAB + модалка) ----
export function QuickTxModal({ categories, accounts = [], onClose, onSaved }: {
  categories: Category[]; accounts?: AccountInfo[]; onClose: () => void; onSaved: () => void;
}) {
  const [type, setType] = useState<TxType>(1);
  const cats = categories.filter(c => c.type === type);
  const [categoryId, setCategoryId] = useState<number>(cats[0]?.id ?? 0);
  const [amount, setAmount] = useState('');
  const [date, setDate] = useState(new Date().toISOString().slice(0, 10));
  const [comment, setComment] = useState('');
  const [accountId, setAccountId] = useState<number | null>(accounts[0]?.id ?? null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');

  useEffect(() => { if (cats.length && !cats.some(c => c.id === categoryId)) setCategoryId(cats[0].id); }, [type]);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setErr(''); setBusy(true);
    try {
      await api.post('/api/transactions', {
        categoryId, type, amount: Number(amount.replace(',', '.')), date, comment, accountId,
      });
      onSaved(); onClose();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  return (
    <div className="modal-back" onClick={onClose}>
      <form className="modal" onClick={e => e.stopPropagation()} onSubmit={submit}>
        <h3>Новая операция</h3>
        <div className="tabs">
          <button type="button" className={`tab ${type === 0 ? 'active' : ''}`} onClick={() => setType(0)}>💰 Доход</button>
          <button type="button" className={`tab ${type === 1 ? 'active' : ''}`} onClick={() => setType(1)}>💸 Расход</button>
        </div>
        <label>Категория
          <select value={categoryId} onChange={e => setCategoryId(Number(e.target.value))}>
            {cats.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>
        <div className="form-row">
          <label>Сумма, ₽<input inputMode="decimal" value={amount} onChange={e => setAmount(e.target.value)} required placeholder="1500" /></label>
          <label>Дата<input type="date" value={date} onChange={e => setDate(e.target.value)} required /></label>
        </div>
        {accounts.length > 0 && (
          <label>Счёт
            <select value={accountId ?? 0} onChange={e => setAccountId(Number(e.target.value) || null)}>
              <option value={0}>Без счёта</option>
              {accounts.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
            </select>
          </label>
        )}
        <label>Комментарий<input value={comment} onChange={e => setComment(e.target.value)} placeholder="необязательно" /></label>
        {err && <div className="alert">{err}</div>}
        <button className="btn primary block" disabled={busy || !cats.length}>{busy ? '…' : 'Сохранить'}</button>
      </form>
    </div>
  );
}

// ---- Журнал операций ----
export function Journal() {
  const now = new Date();
  const [ym, setYm] = useState(`${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`);
  const [txs, setTxs] = useState<Tx[] | null>(null);
  const [err, setErr] = useState('');

  const load = () => {
    const [y, m] = ym.split('-').map(Number);
    setTxs(null);
    api.get<Tx[]>(`/api/transactions?month=${m}&year=${y}&take=500`)
      .then(setTxs).catch(e => setErr(e.message));
  };
  useEffect(load, [ym]);

  const del = async (id: number) => {
    if (!confirm('Удалить операцию?')) return;
    try { await api.del(`/api/transactions/${id}`); load(); }
    catch (e: any) { alert(e.message); }
  };

  const totalIn = (txs ?? []).filter(t => t.type === 0).reduce((s, t) => s + t.amount, 0);
  const totalOut = (txs ?? []).filter(t => t.type === 1).reduce((s, t) => s + t.amount, 0);

  return (
    <div className="card">
      <h3>🗂 Журнал</h3>
      <label>Месяц<input type="month" value={ym} onChange={e => setYm(e.target.value)} /></label>
      {err && <div className="alert">{err}</div>}
      {!txs ? <div className="muted">Загрузка…</div> : txs.length === 0 ? <div className="muted">Нет операций за этот месяц</div> : (
        <>
          <div className="metric-box" style={{ marginBottom: 10 }}>
            <div className="metric pos"><span>+{fmt(totalIn)}</span><small>доходы</small></div>
            <div className="metric neg"><span>−{fmt(totalOut)}</span><small>расходы</small></div>
            <div className="metric"><span>{fmt(totalIn - totalOut)}</span><small>итог</small></div>
          </div>
          {txs.map(t => <TxRow key={t.id} tx={t} onDelete={del} />)}
        </>
      )}
    </div>
  );
}
