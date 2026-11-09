<<<<<<< HEAD
import { useEffect, useRef, useState } from 'react';
import { Summary, Category, Tx, TxType, AccountInfo, api, fmt, MONTHS_RU, AccountAlert, ReserveInfo, ReserveLoan } from './api';
import { useToast } from './toasts';
=======
import { useEffect, useState } from 'react';
import { Summary, Category, Tx, TxType, AccountInfo, AccountType, FamilyBoard, api, fmt, MONTHS_RU } from './api';

const ACC_EMOJI: Record<number, string> = { 0: '💳', 1: '🏦', 2: '💵', 3: '📈', 4: '🧾' };

// ---- Форма счёта (используется на странице «Счета» и в карточке счетов на главной) ----
export function AccountForm({ account, onClose, onSaved }: {
  account: AccountInfo; onClose: () => void; onSaved: () => void;
}) {
  const isNew = account.id === 0;
  const [name, setName] = useState(account.name);
  const [type, setType] = useState<AccountType>(account.type);
  const [balance, setBalance] = useState(String(account.startingBalance ?? account.balance ?? 0));
  const [limit, setLimit] = useState(String(account.creditLimit || ''));
  const [cashback, setCashback] = useState(account.cashbackRate ? String(account.cashbackRate) : '');
  const [color, setColor] = useState(account.color || '#2b6cb0');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');

  const isCredit = type === 1;

  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setErr(''); setBusy(true);
    const body = {
      name, type,
      balance: Number(balance.replace(',', '.')) || 0,
      creditLimit: isCredit ? (Number(limit.replace(',', '.')) || 0) : 0,
      cashbackRate: isCredit ? 0 : (Number(cashback.replace(',', '.')) || 0),
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
      <h4>{isNew ? 'Новый счёт' : `Счёт «${account.name}»`}</h4>
      <label>Название (любое)<br />
        <input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Зарплатная, Халва, Наличные" />
      </label>
      <label>Тип
        <select value={type} onChange={e => setType(Number(e.target.value) as AccountType)}>
          <option value={0}>💳 Дебетовая карта</option>
          <option value={1}>🏦 Кредитная карта</option>
          <option value={2}>💵 Наличные</option>
          <option value={3}>📈 Вклад / накопительный</option>
          <option value={4}>🧾 Прочее</option>
        </select>
      </label>
      <div className="form-row">
        <label>{isCredit ? 'Текущий долг, ₽' : 'Баланс, ₽'}
          <input inputMode="decimal" value={balance} onChange={e => setBalance(e.target.value)} required placeholder="0" />
        </label>
        {isCredit && (
          <label>Кредитный лимит, ₽
            <input inputMode="decimal" value={limit} onChange={e => setLimit(e.target.value)} placeholder="100000" />
          </label>
        )}
        {!isCredit && (
          <label>Кэшбэк, %
            <input inputMode="decimal" value={cashback} onChange={e => setCashback(e.target.value)} placeholder="5" />
          </label>
        )}
        <label>Цвет<input type="color" value={color} onChange={e => setColor(e.target.value)} style={{ height: 40, padding: 2 }} /></label>
      </div>
      <div className="muted">
        {isCredit
          ? 'По кредитной карте баланс = сумма долга; считается доступный лимит и % использования.'
          : cashback ? 'Кэшбэк начисляется на расходы с этого счёта автоматически.' : 'Для наличных кэшбэк не нужен.'}
      </div>
      {err && <div className="alert">{err}</div>}
      <div className="form-row" style={{ gap: 8 }}>
        <button className="btn primary" disabled={busy}>{busy ? '…' : (isNew ? 'Создать счёт' : 'Сохранить')}</button>
        <button type="button" className="btn" onClick={onClose}>Отмена</button>
      </div>
    </form>
  );
}

// ---- Карточка на главной: колонка на пользователя семьи, внутри — счета с балансами.
// Счета прямоугольные; управление доступно для всех счетов семьи (редактирование, корректировка баланса, удаление).
function AccountsBoardCard() {
  const [board, setBoard] = useState<FamilyBoard | null>(null);
  const [editing, setEditing] = useState<AccountInfo | null>(null);
  const load = () => {
    api.get<FamilyBoard>('/api/family/family-board').then(setBoard).catch(() => setBoard({ familyCode: null, columns: [] }));
  };
  useEffect(load, []);
  if (!board) return <div className="card muted">Загрузка счетов…</div>;
  const cols = board.columns;
  const commonTotal = cols.reduce((s, c) => s + c.totalBalance, 0);

  const del = async (a: AccountInfo) => {
    if (!confirm(`Удалить счёт «${a.name}»? Операции останутся в журнале без счёта.`)) return;
    try { await api.del(`/api/accounts/${a.id}`); load(); } catch (e: any) { alert(e.message); }
  };
  const adjust = async (a: AccountInfo) => {
    const raw = prompt(`Корректировка баланса «${a.name}» (можно со знаком −), ₽`, '-5000');
    if (raw === null) return;
    const amount = Number(raw.replace(',', '.')) || 0;
    if (amount === 0) { alert('Укажите сумму'); return; }
    try { await api.post(`/api/accounts/${a.id}/adjust`, { delta: amount }); load(); } catch (e: any) { alert(e.message); }
  };

  return (
    <div className="card">
      <h3>💳 Счета{board.familyCode ? ' семьи' : ''}{board.familyCode && <span className="muted"> · код {board.familyCode}</span>}</h3>
      {cols.length === 0 || cols.every(c => c.accounts.length === 0)
        ? <div className="muted">Счетов пока нет — добавьте их на странице «Счета».</div> : (
        <>
          <div className="board-columns">
            {cols.map(c => (
              <div className="board-col" key={c.userId}>
                <div className="board-col-head">
                  <b>{c.name}{c.isMe ? ' (вы)' : ''}</b>
                  {c.isOwner && <span className="badge">создатель</span>}
                </div>
                <div className={`board-col-total ${c.totalBalance >= 0 ? 'pos' : 'neg'}`}>{fmt(c.totalBalance)}</div>
                {c.accounts.length === 0
                  ? <div className="muted small">нет счетов</div>
                  : c.accounts.map(a => (
                    <div className="board-acc" key={a.id} style={{ borderLeftColor: a.color }}>
                      <span>{ACC_EMOJI[a.type]} {a.name}</span>
                      <b className={a.type === 1 ? 'neg' : ''}>{fmt(a.balance)}</b>
                      {a.type === 0 && a.cashbackRate > 0 && <small className="muted">кэшбэк {a.cashbackRate}%</small>}
                      {a.type === 1 && a.creditLimit > 0 && <small className="muted">лимит {fmt(a.creditLimit)} · доступно {fmt(a.available)}</small>}
                      <div className="board-acc-actions">
                        <button className="btn tiny" title="Редактировать" onClick={() => setEditing({ ...a })}>✏️</button>
                        <button className="btn tiny" title="Пополнить / списать" onClick={() => adjust(a)}>±</button>
                        <button className="btn tiny danger-ghost" title="Удалить" onClick={() => del(a)}>✕</button>
                      </div>
                    </div>
                  ))}
              </div>
            ))}
          </div>
          {cols.length > 1 && (
            <div className={`metric ${commonTotal >= 0 ? 'pos' : 'neg'}`} style={{ marginTop: 8 }}>
              <span>{fmt(commonTotal)}</span><small>вместе по семье</small>
            </div>
          )}
        </>
      )}
      {editing && (
        <AccountForm account={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); load(); }} />
      )}
    </div>
  );
}
>>>>>>> bf4507991471ab9d18e3eda494a9d6cab04eb3b0

// ---- Дашборд: обзор бюджета + анализ + рекомендации ----
export function Dashboard() {
  const [sum, setSum] = useState<Summary | null>(null);
  const [recent, setRecent] = useState<Tx[]>([]);
  const [err, setErr] = useState('');
  const toast = useToast();
  const seenAlerts = useRef<Set<number>>(new Set());

  const reload = () => {
    const now = new Date();
    Promise.all([
      api.get<Summary>('/api/budget/summary'),
      api.get<Tx[]>(`/api/transactions?month=${now.getMonth() + 1}&year=${now.getFullYear()}&take=8`),
    ]).then(([s, t]) => { setSum(s); setRecent(t); }).catch(e => setErr(e.message));
  };
  useEffect(reload, []);

  // Тосты на новые активные уведомления (после закрытия/развязки не спамим).
  useEffect(() => {
    const alerts = sum?.alerts ?? [];
    for (const a of alerts) {
      if (seenAlerts.current.has(a.id)) continue;
      seenAlerts.current.add(a.id);
      toast(a.level === 'danger' ? 'danger' : 'warn', `⚠️ ${a.message}`);
    }
  }, [sum, toast]);

  const dismissAlert = async (id: number) => {
    try { await api.post(`/api/alerts/${id}/dismiss`); reload(); } catch { /* ignore */ }
  };
  const returnLoan = async (id: number) => {
    try { await api.post(`/api/reserve-loans/${id}/return`, {}); reload(); } catch { /* ignore */ }
  };

  if (err) return <div className="alert">{err}</div>;
  if (!sum) return <div className="muted card">Загрузка…</div>;

  const months = sum.byMonth.slice(-6);
  const maxVal = Math.max(...months.flatMap(m => [m.income, m.expense]), 1);
  const totalExpense = sum.expenseShare.reduce((s, x) => s + x.total, 0);

  return (
    <>
      <AccountsBoardCard />
      <div className="metric-box">
        <div className="metric"><span>{fmt(sum.monthIncome)}</span><small>Доход за месяц</small></div>
        <div className="metric"><span>{fmt(sum.monthExpense)}</span><small>Расход за месяц</small></div>
        <div className={`metric ${sum.balance >= 0 ? 'pos' : 'neg'}`}>
          <span>{fmt(sum.balance)}</span><small>Итог месяца</small></div>
        <div className={`metric ${sum.totalBalance >= 0 ? 'pos' : 'neg'}`}>
          <span>{fmt(sum.totalBalance)}</span><small>Баланс с учётом счетов</small></div>
      </div>

      <div className="card">
        <h3>Персональные рекомендации</h3>
        {sum.recommendations.map((r, i) => (
          <div key={i} className={`rec rec-${r.level}`}>
            <span className="rec-icon">{r.icon}</span>
            <span>{r.text}</span>
          </div>
        ))}
      </div>

      <div className="card">
        <h3>Динамика (6 мес.) · норма сбережений {sum.savingsRate}%</h3>
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
        <h3>Структура расходов ({MONTHS_RU[sum.month - 1]})</h3>
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
        <h3>Лимиты категорий ({MONTHS_RU[sum.month - 1]})</h3>
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
          <h3>Финансовые цели</h3>
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

      {sum.alerts.length > 0 && (
        <div className="card">
          <h3>Уведомления по правилам счетов</h3>
          {sum.alerts.map(a => (
            <div key={a.id} className={`alert-row ${a.level === 'danger' ? 'alert-danger' : 'alert-warn'}`}>
              <div className="alert-msg">{a.message}</div>
              <div className="muted" style={{ marginTop: 2 }}>
                {a.kind === 'MinBalance' ? 'Неснижаемый остаток'
                  : a.kind === 'ReserveBroken' ? 'Нарушение резерва'
                  : a.kind === 'ReserveOverdue' ? 'Просроченный заём' : a.kind}
                {a.accountName ? ` · счёт «${a.accountName}»` : ''}
              </div>
              <div style={{ marginTop: 6 }}>
                <button className="btn small" onClick={() => dismissAlert(a.id)}>✓ Увидел</button>
              </div>
            </div>
          ))}
        </div>
      )}

      {sum.reserves.length > 0 && (
        <div className="card">
          <h3>Резерв на экстренные нужды</h3>
          {sum.reserves.map(r => {
            const pct = r.threshold > 0 ? Math.min(100, (r.free / r.threshold) * 100) : 0;
            const broken = r.free <= 0;
            return (
              <div className="limit-row" key={r.ruleId}>
                <div className="limit-head">
                  <span><b>{r.accountName ?? 'Общий резерв'}</b> · {r.ruleName}</span>
                  <span className={broken ? 'badge badge-danger' : 'badge badge-muted'}>
                    свободно {fmt(r.free)} из {fmt(r.threshold)}
                  </span>
                </div>
                <div className="progress">
                  <div className={broken ? 'over' : ''} style={{ width: `${pct}%`, background: broken ? undefined : 'var(--success)' }} />
                </div>
                <div className="muted" style={{ marginTop: 2 }}>
                  Резерв {fmt(r.threshold)} · взято из резерва {fmt(r.taken)}
                  {(() => {
                    const loan = sum.reserveLoans.find(l => l.ruleId === r.ruleId);
                    return loan ? ` · вернуть до ${new Date(loan.dueDate).toLocaleDateString('ru-RU')}` : '';
                  })()}
                </div>
              </div>
            );
          })}
          {sum.reserveLoans.length > 0 && (
            <div style={{ marginTop: 8 }}>
              {sum.reserveLoans.map(l => (
                <div className="tx-item" key={l.id}>
                  <div className="tx-main">
                    <div className="t">Заём из резерва: {fmt(l.amount)}</div>
                    <div className="s">
                      {l.accountName ? `счёт «${l.accountName}» · ` : ''}
                      {l.comment ? `${l.comment} · ` : ''}
                      срок возврата {new Date(l.dueDate).toLocaleDateString('ru-RU')}
                      {l.isOverdue ? ' · ПРОСРОЧЕН' : ''}
                    </div>
                  </div>
                  <button className="btn small" onClick={() => returnLoan(l.id)}>Вернул</button>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      <div className="card">
        <h3>Последние операции</h3>
        {recent.length === 0 ? <div className="muted">В этом месяце операций нет. Нажмите + внизу справа.</div> :
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
        <div className="s">{new Date(tx.date).toLocaleDateString('ru-RU')}{tx.accountName ? ` · ${tx.accountName}` : ''}{tx.isTransfer ? ' · перевод' : ''}</div>
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
  const [reserves, setReserves] = useState<ReserveInfo[]>([]);
  const [warn, setWarn] = useState<{ reserve: ReserveInfo; amount: number } | null>(null);

  useEffect(() => { if (cats.length && !cats.some(c => c.id === categoryId)) setCategoryId(cats[0].id); }, [type]);
  useEffect(() => {
    api.get<{ reserves: ReserveInfo[] }>('/api/budget/summary').then(s => setReserves(s.reserves ?? [])).catch(() => {});
  }, []);

  const saveTxn = async () => {
    await api.post('/api/transactions', {
      categoryId, type, amount: Number(amount.replace(',', '.')), date, comment, accountId,
    });
    onSaved(); onClose();
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setErr(''); setBusy(true);
    const acc = accounts.find(a => a.id === accountId) || null;
    const amountVal = Number(amount.replace(',', '.')) || 0;
    const reserve = type === 1 && acc ? reserves.find(r => r.accountId === acc.id || r.accountId === null) : undefined;
    // Расход мог бы опустить баланс ниже свободного резерва — мягкое предупреждение + оформление займа.
    if (reserve && acc && acc.balance - amountVal < reserve.free) {
      setWarn({ reserve, amount: amountVal });
      setBusy(false);
      return;
    }
    try { await saveTxn(); } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const takeLoan = async () => {
    if (!warn) return;
    setBusy(true); setErr('');
    try {
      await api.post('/api/reserve-loans', { accountId: accountId!, amount: warn.amount, ruleId: warn.reserve.ruleId });
      await saveTxn();
      setWarn(null);
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  return (
    <div className="sk-modal-back" onClick={onClose}>
      <form className="sk-modal" onClick={e => e.stopPropagation()} onSubmit={submit}>
        <h3>Новая операция</h3>
        <div className="tabs">
          <button type="button" className={`tab ${type === 0 ? 'active' : ''}`} onClick={() => setType(0)}>Доход</button>
          <button type="button" className={`tab ${type === 1 ? 'active' : ''}`} onClick={() => setType(1)}>Расход</button>
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
        {warn && (
          <div className={`alert-row ${warn.reserve.free <= 0 ? 'alert-danger' : 'alert-warn'}`}>
            <div className="alert-msg">
              ⚠️ Трата {fmt(warn.amount)} может затронуть резерв «{warn.reserve.ruleName}» (свободно {fmt(warn.reserve.free)}).
              Оформить это как заём из резерва?
            </div>
            <div className="form-row" style={{ gap: 8, marginTop: 8 }}>
              <button type="button" className="btn primary small" disabled={busy} onClick={takeLoan}>Оформить заём из резерва</button>
              <button type="button" className="btn small" disabled={busy} onClick={() => setWarn(null)}>Просто сохранить</button>
            </div>
          </div>
        )}
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

  const totalIn = (txs ?? []).filter(t => t.type === 0 && !t.isTransfer).reduce((s, t) => s + t.amount, 0);
  const totalOut = (txs ?? []).filter(t => t.type === 1 && !t.isTransfer).reduce((s, t) => s + t.amount, 0);

  return (
    <div className="card">
      <h3>Журнал</h3>
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
