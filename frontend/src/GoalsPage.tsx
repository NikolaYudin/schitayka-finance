import { useEffect, useState } from 'react';
import { Category, Goal, api, fmt, MONTHS_RU } from './api';

// ---- Экран «Цели»: финансовые цели + лимиты бюджета на месяц ----
export function GoalsPage() {
  const [tab, setTab] = useState<'goals' | 'limits'>('goals');
  return (
    <>
      <div className="card">
        <div className="tabs">
          <button className={`tab ${tab === 'goals' ? 'active' : ''}`} onClick={() => setTab('goals')}>🎯 Цели</button>
          <button className={`tab ${tab === 'limits' ? 'active' : ''}`} onClick={() => setTab('limits')}>📊 Лимиты месяца</button>
        </div>
        {tab === 'goals' ? <Goals /> : <Limits />}
      </div>
    </>
  );
}

function Goals() {
  const [goals, setGoals] = useState<Goal[] | null>(null);
  const [name, setName] = useState('');
  const [target, setTarget] = useState('');
  const [deadline, setDeadline] = useState('');

  const load = () => api.get<Goal[]>('/api/goals').then(setGoals);
  useEffect(() => { load(); }, []);

  const add = async (e: React.FormEvent) => {
    e.preventDefault();
    try {
      await api.post('/api/goals', {
        name, targetAmount: Number(target),
        deadline: deadline ? deadline : null,
      });
      setName(''); setTarget(''); setDeadline(''); load();
    } catch (ex: any) { alert(ex.message); }
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
      {!goals ? <div className="muted">Загрузка…</div> : goals.length === 0 ? <div className="muted">Целей пока нет — поставьте первую!</div> :
        goals.map(g => (
          <div className="limit-row" key={g.id}>
            <div className="limit-head">
              <span><b>{g.name}</b>{g.deadline && <span className="muted"> · до {new Date(g.deadline).toLocaleDateString('ru-RU')}</span>}</span>
              <span className="muted">{fmt(g.currentAmount)} / {fmt(g.targetAmount)}</span>
            </div>
            <div className="progress"><div style={{ width: `${Math.min(100, g.progress)}%`, background: 'var(--success)' }} /></div>
            {g.perMonth !== null && g.monthsLeft !== null && (
              <div className="muted" style={{ marginTop: 4 }}>
                Нужно откладывать {fmt(g.perMonth)}/мес — осталось {g.monthsLeft} мес.
              </div>
            )}
            <div style={{ marginTop: 4 }}>
              <button className="btn small" onClick={() => deposit(g)}>± Пополнить</button>{' '}
              <button className="btn danger-ghost small" onClick={() => del(g.id)}>✕</button>
            </div>
          </div>
        ))}
      <form onSubmit={add} style={{ marginTop: 12 }}>
        <div className="form-row">
          <label>Название цели<input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Отпуск" /></label>
          <label>Целевая сумма, ₽<input inputMode="numeric" value={target} onChange={e => setTarget(e.target.value)} required placeholder="150000" /></label>
          <label>Срок (необязательно)<input type="date" value={deadline} onChange={e => setDeadline(e.target.value)} /></label>
        </div>
        <button className="btn primary">+ Создать цель</button>
      </form>
    </>
  );
}

interface LimitRowClient { id: number; categoryId: number; year: number; month: number; limit: number; categoryName: string; }

function Limits() {
  const now = new Date();
  const [ym, setYm] = useState(`${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`);
  const [cats, setCats] = useState<Category[]>([]);
  const [limits, setLimits] = useState<LimitRowClient[] | null>(null);
  const [categoryId, setCategoryId] = useState<number>(0);
  const [amount, setAmount] = useState('');
  const [err, setErr] = useState('');

  const expenseCats = cats.filter(c => c.type === 1);

  const load = () => {
    const [y, m] = ym.split('-').map(Number);
    api.get<LimitRowClient[]>(`/api/limits?year=${y}&month=${m}`).then(setLimits).catch(e => setErr(e.message));
  };
  useEffect(() => { api.get<Category[]>('/api/categories').then(cs => {
    setCats(cs);
    const firstExpense = cs.find(c => c.type === 1);
    if (firstExpense) setCategoryId(firstExpense.id);
  }); }, []);
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

  const [yy, mm] = ym.split('-').map(Number);

  return (
    <>
      <label>Месяц<input type="month" value={ym} onChange={e => setYm(e.target.value)} /></label>
      {err && <div className="alert">{err}</div>}
      {!limits ? <div className="muted">Загрузка…</div> : limits.length === 0 ? (
        <div className="muted">На {MONTHS_RU[mm - 1]} {yy} лимитов нет</div>
      ) : (
        <table>
          <thead><tr><th>Категория</th><th className="num">Лимит</th><th></th></tr></thead>
          <tbody>
            {limits.map(l => (
              <tr key={l.id}>
                <td>{l.categoryName}</td>
                <td className="num">{fmt(l.limit)}</td>
                <td><button className="btn danger-ghost small" onClick={() => del(l.id)}>✕</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <form onSubmit={save} style={{ marginTop: 12 }}>
        <div className="form-row">
          <label>Категория расхода
            <select value={categoryId} onChange={e => setCategoryId(Number(e.target.value))}>
              {expenseCats.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
          <label>Лимит на {MONTHS_RU[mm - 1].toLowerCase()} {yy}, ₽
            <input inputMode="numeric" value={amount} onChange={e => setAmount(e.target.value)} required placeholder="8000" />
          </label>
        </div>
        <button className="btn primary" disabled={!expenseCats.length}>+ Задать лимит</button>
      </form>
      <p className="muted" style={{ marginTop: 8 }}>
        Прогресс по лимитам и рекомендации по их превышению видны в разделе «Обзор».
      </p>
    </>
  );
}
