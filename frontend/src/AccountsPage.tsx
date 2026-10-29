import { useEffect, useState } from 'react';
import { AccountInfo, AccountType, FamilyMember, api, fmt } from './api';

const TYPE_EMOJI: Record<AccountType, string> = { 0: '💳', 1: '🏦', 2: '💵', 3: '📈', 4: '🧾' };

// ---- Страница «Счета»: карточки счетов + форма добавления/редактирования ----
export function AccountsPage({ onChanged }: { onChanged?: () => void }) {
  const [list, setList] = useState<AccountInfo[] | null>(null);
  const [err, setErr] = useState('');
  const [editing, setEditing] = useState<AccountInfo | null>(null); // null = нет формы, объект (id 0) = новая
  const [showArchived, setShowArchived] = useState(false);

  const load = () => {
    api.get<AccountInfo[]>(`/api/accounts?includeArchived=${showArchived}`)
      .then(setList).catch(e => setErr(e.message));
  };
  useEffect(load, [showArchived]);

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

  const total = (list ?? []).filter(a => !a.isArchived && a.type !== 1)
    .reduce((s, a) => s + a.balance, 0);
  const debt = (list ?? []).filter(a => !a.isArchived && a.type === 1)
    .reduce((s, a) => s + a.balance, 0);

  return (
    <div className="card">
      <h3>💳 Счета</h3>
      {err && <div className="alert">{err}</div>}
      {!list ? <div className="muted">Загрузка…</div> : (
        <>
          <div className="metric-box" style={{ marginBottom: 10 }}>
            <div className="metric pos"><span>{fmt(total)}</span><small>на счетах</small></div>
            <div className={`metric ${debt > 0 ? 'neg' : ''}`}><span>{fmt(debt)}</span><small>долг по кредиткам</small></div>
          </div>
          {list.length === 0 && <div className="muted">Счетов пока нет — добавьте первый.</div>}
          {list.map(a => (
            <div className="account-card" key={a.id} style={{ borderLeftColor: a.color }}>
              <div className="account-head">
                <b>{TYPE_EMOJI[a.type]} {a.name}</b>
                <span className="muted">{a.typeName}{a.isArchived ? ' · архив' : ''}</span>
              </div>
              <div className="account-balance">{fmt(a.balance)}</div>
              {a.type === 1 && a.creditLimit > 0 && (
                <div className="limit-row">
                  <div className="limit-head">
                    <span className="muted">Лимит {fmt(a.creditLimit)} · доступно {fmt(a.available)}</span>
                    <span className="muted">{a.creditUsagePct}%</span>
                  </div>
                  <div className="progress">
                    <div className={a.creditUsagePct! > 80 ? 'over' : ''} style={{ width: `${Math.min(100, a.creditUsagePct ?? 0)}%` }} />
                  </div>
                </div>
              )}
              {a.type === 0 && a.cashbackRate > 0 && (
                <div className="muted" style={{ marginTop: 2 }}>кэшбэк {a.cashbackRate}% · начислено {fmt(a.cashbackAccrued)}</div>
              )}
              <div className="account-actions">
                <button className="btn small" onClick={() => setEditing({ ...a })}>✏️</button>
                {a.type !== 1 && a.cashbackRate >= 0 && (
                  <button className="btn small" title="Зачислить кэшбэк" onClick={() => adjust(a, 'cashback')}>🎁</button>
                )}
                <button className="btn small" title="Корректировать баланс" onClick={() => adjust(a, 'adjust')}>±</button>
                <button className="btn small" title={a.isArchived ? 'Вернуть из архива' : 'В архив'} onClick={() => archive(a)}>🗄</button>
                <button className="btn danger-ghost small" onClick={() => del(a)}>✕</button>
              </div>
            </div>
          ))}
          <label className="muted" style={{ display: 'flex', gap: 6, alignItems: 'center', marginTop: 8 }}>
            <input type="checkbox" checked={showArchived} onChange={e => setShowArchived(e.target.checked)} />
            показывать архивные
          </label>
        </>
      )}
      <button className="btn primary block" onClick={() => setEditing({
        id: 0, name: '', type: 0 as AccountType, typeName: 'Дебетовая карта', balance: 0, startingBalance: 0,
        creditLimit: 0, cashbackRate: 0, cashbackAccrued: 0, color: '#2b6cb0', isArchived: false, available: 0, creditUsagePct: null,
      })}>+ Добавить счёт</button>

      {editing && (
        <AccountForm account={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); afterChange(); }} />
      )}
    </div>
  );
}

function AccountForm({ account, onClose, onSaved }: {
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
    if (inviteCode) navigator.clipboard?.writeText(inviteCode).then(() => setMsg('Код скопирован ✓')).catch(() => {});
  };

  return (
    <div className="card">
      <h3>👨‍👩‍👧 Семья</h3>
      {err && <div className="alert">{err}</div>}
      {!members ? <div className="muted">Загрузка…</div> : members.length > 0 ? (
        <>
          <table>
            <thead><tr><th>Участник</th><th>Email</th></tr></thead>
            <tbody>
              {members.map(m => <tr key={m.id}><td><b>{m.name}</b></td><td className="muted">{m.email}</td></tr>)}
            </tbody>
          </table>
          <div className="muted" style={{ margin: '6px 0' }}>Код семьи: {familyCode}</div>
          {inviteCode && (
            <div className="rec rec-info">
              <span className="rec-icon">🎟</span>
              <span>Пригласительный код: <b style={{ letterSpacing: 2 }}>{inviteCode}</b>
                {expiresAt && <span className="muted"> · до {new Date(expiresAt).toLocaleDateString('ru-RU')}</span>}
              </span>
              <button className="btn small" onClick={copy}>копировать</button>
            </div>
          )}
          <div className="form-row" style={{ gap: 8, marginTop: 8 }}>
            <button className="btn primary small" onClick={invite}>🎟 Созвать семью (новый код)</button>
            <button className="btn danger-ghost small" onClick={leave}>Выйти из семьи</button>
          </div>
        </>
      ) : (
        <>
          <div className="muted" style={{ marginBottom: 8 }}>
            Вы пока не в семье. Создайте семью и отправьте близким пригласительный код —
            либо введите код, который получили от них.
          </div>
          <button className="btn primary" onClick={invite}>🎟 Создать семью и получить код</button>
          {inviteCode && (
            <div className="rec rec-info" style={{ marginTop: 8 }}>
              <span className="rec-icon">🎟</span>
              <span>Передайте код: <b style={{ letterSpacing: 2 }}>{inviteCode}</b>
                {expiresAt && <span className="muted"> · действует до {new Date(expiresAt).toLocaleDateString('ru-RU')}</span>}</span>
              <button className="btn small" onClick={copy}>копировать</button>
            </div>
          )}
          <form onSubmit={join} style={{ marginTop: 10 }}>
            <div className="form-row">
              <label>У меня есть код
                <input value={joinCode} onChange={e => setJoinCode(e.target.value)} placeholder="напр. K7M2QX" maxLength={6}
                  style={{ textTransform: 'uppercase' }} />
              </label>
              <button className="btn small" style={{ alignSelf: 'end' }}>Присоединиться</button>
            </div>
          </form>
        </>
      )}
      {msg && <div className="muted">{msg}</div>}
    </div>
  );
}
