import { useEffect, useState } from 'react';
import { Category, Goal, Tx, api, fmt, MONTHS_RU } from './api';
import { Icon } from './icons';

const pad2 = (n: number) => String(n).padStart(2, '0');

interface LimitEntry {
  id: number;
  categoryId: number;
  year: number;
  month: number;
  limit: number;
  categoryName: string;
}

// ---- Экран «Цели» и «Бюджет и лимиты» ----
export function GoalsPage({ initialTab = 'goals' }: { initialTab?: 'goals' | 'limits' }) {
  const [tab, setTab] = useState<'goals' | 'limits'>(initialTab);
  useEffect(() => setTab(initialTab), [initialTab]);

  return (
    <div className="card">
      <div className="card-head" style={{ flexWrap: 'wrap' }}>
        <div>
          <h3>{tab === 'goals' ? 'Финансовые цели' : 'Лимиты месяца'}</h3>
          <p className="card-sub">
            {tab === 'goals'
              ? 'Копите осознанно: цель, срок и сумма ежемесячного взноса'
              : 'Сколько запланировано и сколько уже потрачено по категориям'}
          </p>
        </div>
        <div className="segmented">
          <button type="button" className={`segment ${tab === 'goals' ? 'active' : ''}`} onClick={() => setTab('goals')}>
            Цели
          </button>
          <button type="button" className={`segment ${tab === 'limits' ? 'active' : ''}`} onClick={() => setTab('limits')}>
            Лимиты месяца
          </button>
        </div>
      </div>

      {tab === 'goals' ? <Goals /> : <Limits />}
    </div>
  );
}

// ============================================================================
// Цели
// ============================================================================
function Goals() {
  const [goals, setGoals] = useState<Goal[] | null>(null);
  const [name, setName] = useState('');
  const [target, setTarget] = useState('');
  const [deadline, setDeadline] = useState('');
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);

  const load = () => api.get<Goal[]>('/api/goals').then(setGoals).catch(e => setErr(e.message));
  useEffect(() => { load(); }, []);

  const add = async (e: React.FormEvent) => {
    e.preventDefault(); setErr(''); setBusy(true);
    try {
      await api.post('/api/goals', {
        name, targetAmount: Number(target.replace(',', '.')),
        deadline: deadline ? deadline : null,
      });
      setName(''); setTarget(''); setDeadline(''); load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };
  const deposit = async (g: Goal) => {
    const v = prompt(`Пополнить «${g.name}» на сумму, ₽ (можно со знаком − для снятия):`);
    if (v === null || !v.trim()) return;
    try { await api.post(`/api/goals/${g.id}/deposit`, { delta: Number(v.replace(',', '.')) }); load(); }
    catch (ex: any) { alert(ex.message); }
  };
  const del = async (id: number) => {
    if (!confirm('Удалить цель?')) return;
    try { await api.del(`/api/goals/${id}`); load(); } catch (ex: any) { alert(ex.message); }
  };

  return (
    <>
      {!goals ? (
        <div className="empty"><div className="empty-text">Загрузка целей…</div></div>
      ) : goals.length === 0 ? (
        <div className="empty">
          <span className="empty-ico"><Icon name="target" size={22} /></span>
          <div className="empty-title">Целей пока нет</div>
          <div className="empty-text">Поставьте первую цель — отпуск, техника или подушка безопасности.</div>
        </div>
      ) : (
        <div className="goals-grid">
          {goals.map(g => {
            const done = g.progress >= 100;
            return (
              <div className="goal-card" key={g.id}>
                <div className="goal-top">
                  <div>
                    <div className="goal-name">{g.name}</div>
                    <div className="goal-deadline">
                      {g.deadline ? `до ${new Date(g.deadline).toLocaleDateString('ru-RU')}` : 'без срока'}
                    </div>
                  </div>
                  <span className="goal-badge"><Icon name="target" size={20} /></span>
                </div>

                <div className="goal-amounts">
                  <b>{fmt(g.currentAmount)}</b>
                  <span>из {fmt(g.targetAmount)}</span>
                </div>
                <div className="progress">
                  <div style={{ width: `${Math.min(100, g.progress)}%` }} />
                </div>

                <div className="goal-per">
                  {done
                    ? 'Цель достигнута — можно снимать с копилки.'
                    : g.perMonth !== null && g.monthsLeft !== null
                      ? <>Откладывайте <b style={{ color: 'var(--text-2)' }}>{fmt(g.perMonth)}</b>/мес · осталось {g.monthsLeft} мес.</>
                      : 'Добавьте срок, чтобы рассчитать ежемесячный взнос.'}
                </div>

                <div className="goal-actions">
                  <button className="btn" type="button" onClick={() => deposit(g)}>
                    <Icon name="plus" size={14} /> Пополнить
                  </button>
                  <button className="btn icon danger" style={{ width: 30, height: 30 }} type="button" title="Удалить цель" onClick={() => del(g.id)}>
                    <Icon name="trash" size={14} />
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      <form onSubmit={add} className="account-form">
        <h4>Новая цель</h4>
        <div className="form-row">
          <label style={{ flex: '1.4 1 180px' }}>Название
            <input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Отпуск" />
          </label>
          <label>Целевая сумма, ₽
            <input inputMode="numeric" value={target} onChange={e => setTarget(e.target.value)} required placeholder="150000" />
          </label>
          <label>Срок
            <input type="date" value={deadline} onChange={e => setDeadline(e.target.value)} />
          </label>
        </div>
        {err && <div className="alert alert-danger">{err}</div>}
        <button className="btn primary" type="submit" disabled={busy}>
          <Icon name="plus" size={16} /> {busy ? 'Создаём…' : 'Создать цель'}
        </button>
      </form>
    </>
  );
}

// ============================================================================
// Лимиты
// ============================================================================
function Limits() {
  const now = new Date();
  const [ym, setYm] = useState(`${now.getFullYear()}-${pad2(now.getMonth() + 1)}`);
  const [cats, setCats] = useState<Category[]>([]);
  const [limits, setLimits] = useState<LimitEntry[] | null>(null);
  const [spent, setSpent] = useState<Record<number, number>>({});
  const [categoryId, setCategoryId] = useState<number>(0);
  const [amount, setAmount] = useState('');
  const [err, setErr] = useState('');

  const expenseCats = cats.filter(c => c.type === 1);

  const load = () => {
    const [y, m] = ym.split('-').map(Number);
    setErr('');
    Promise.all([
      api.get<LimitEntry[]>(`/api/limits?year=${y}&month=${m}`),
      api.get<Tx[]>(`/api/transactions?month=${m}&year=${y}&take=500`).catch(() => [] as Tx[]),
    ]).then(([ls, txs]) => {
      setLimits(ls);
      const map: Record<number, number> = {};
      for (const t of txs) {
        if (t.type !== 1 || t.isTransfer) continue;
        map[t.categoryId] = (map[t.categoryId] ?? 0) + t.amount;
      }
      setSpent(map);
    }).catch(e => setErr(e.message));
  };

  useEffect(() => {
    api.get<Category[]>('/api/categories').then(cs => {
      setCats(cs);
      const firstExpense = cs.find(c => c.type === 1);
      if (firstExpense) setCategoryId(firstExpense.id);
    }).catch(() => {});
  }, []);
  useEffect(load, [ym]);

  const save = async (e: React.FormEvent) => {
    e.preventDefault(); setErr('');
    const [y, m] = ym.split('-').map(Number);
    try {
      await api.post('/api/limits', { categoryId, year: y, month: m, limit: Number(amount.replace(',', '.')) });
      setAmount(''); load();
    } catch (ex: any) { setErr(ex.message); }
  };
  const del = async (id: number) => {
    if (!confirm('Удалить лимит?')) return;
    try { await api.del(`/api/limits/${id}`); load(); } catch (ex: any) { alert(ex.message); }
  };

  const shift = (delta: number) => {
    const [y, m] = ym.split('-').map(Number);
    const d = new Date(y, m - 1 + delta, 1);
    setYm(`${d.getFullYear()}-${pad2(d.getMonth() + 1)}`);
  };

  const [yy, mm] = ym.split('-').map(Number);
  const totalLimit = (limits ?? []).reduce((s, l) => s + l.limit, 0);
  const totalSpent = (limits ?? []).reduce((s, l) => s + (spent[l.categoryId] ?? 0), 0);

  return (
    <>
      <div className="card-head" style={{ flexWrap: 'wrap' }}>
        <div>
          <div className="section-title" style={{ fontSize: 16 }}>{MONTHS_RU[mm - 1]} {yy}</div>
          <p className="card-sub">Лимиты на расходы по категориям</p>
        </div>
        <div className="month-nav">
          <button type="button" className="btn icon" onClick={() => shift(-1)} aria-label="Предыдущий месяц">
            <Icon name="chevron-left" size={17} />
          </button>
          <input type="month" value={ym} onChange={e => setYm(e.target.value)} aria-label="Месяц" />
          <button type="button" className="btn icon" onClick={() => shift(1)} aria-label="Следующий месяц">
            <Icon name="chevron-right" size={17} />
          </button>
        </div>
      </div>

      {err && <div className="alert alert-danger">{err}</div>}

      {!limits ? (
        <div className="empty"><div className="empty-text">Загрузка лимитов…</div></div>
      ) : limits.length === 0 ? (
        <div className="empty">
          <span className="empty-ico"><Icon name="shield" size={22} /></span>
          <div className="empty-title">На {MONTHS_RU[mm - 1].toLowerCase()} лимитов нет</div>
          <div className="empty-text">Задайте лимит для категории — приложение предупредит о перерасходе.</div>
        </div>
      ) : (
        <>
          <div className="metric-box" style={{ marginBottom: 18 }}>
            <div className="metric"><span>{fmt(totalLimit)}</span><small>запланировано</small></div>
            <div className={`metric ${totalSpent > totalLimit ? 'neg' : ''}`}><span>{fmt(totalSpent)}</span><small>потрачено</small></div>
            <div className={`metric ${totalLimit - totalSpent >= 0 ? 'pos' : 'neg'}`}>
              <span>{fmt(totalLimit - totalSpent)}</span><small>осталось</small>
            </div>
          </div>

          {limits.map(l => {
            const used = spent[l.categoryId] ?? 0;
            const pct = l.limit > 0 ? Math.min(100, (used / l.limit) * 100) : 0;
            const over = used > l.limit;
            const near = !over && pct >= 80;
            const tone = over ? 'var(--red)' : near ? 'var(--orange)' : 'var(--green)';
            return (
              <div className="limit-row" key={l.id}>
                <div className="limit-head">
                  <span className="limit-name">
                    <i className="dot" style={{ background: expenseCats.find(c => c.id === l.categoryId)?.color ?? 'var(--muted)' }} /> {l.categoryName}
                    {over && <span className="badge badge-danger">перерасход</span>}
                    {near && <span className="badge badge-warn">близко к лимиту</span>}
                  </span>
                  <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                    <span className={`limit-vals ${over ? 'over' : ''}`}>{fmt(used)} / {fmt(l.limit)}</span>
                    <button
                      type="button"
                      className="btn icon danger"
                      style={{ width: 28, height: 28 }}
                      title="Удалить лимит"
                      onClick={() => del(l.id)}
                    >
                      <Icon name="x" size={13} />
                    </button>
                  </span>
                </div>
                <div className="progress">
                  <div style={{ width: `${pct}%`, background: tone }} />
                </div>
                <div className="muted" style={{ marginTop: 4, fontSize: 11.5 }}>
                  {over ? `Превышение на ${fmt(used - l.limit)}` : `Свободно ${fmt(Math.max(0, l.limit - used))}`}
                </div>
              </div>
            );
          })}
        </>
      )}

      <form onSubmit={save} className="account-form">
        <h4>Задать лимит</h4>
        <div className="form-row">
          <label>Категория расхода
            <select value={categoryId} onChange={e => setCategoryId(Number(e.target.value))}>
              {expenseCats.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
          <label>Лимит на {MONTHS_RU[mm - 1].toLowerCase()}, ₽
            <input inputMode="numeric" value={amount} onChange={e => setAmount(e.target.value)} required placeholder="8000" />
          </label>
        </div>
        <button className="btn primary" type="submit" disabled={!expenseCats.length}>
          <Icon name="check" size={16} /> Сохранить лимит
        </button>
      </form>
    </>
  );
}
