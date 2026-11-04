import { useEffect, useState } from 'react';
import { AccountInfo, AccountType, FamilyMember, api, fmt } from './api';
import { AccountForm } from './components';

const TYPE_EMOJI: Record<AccountType, string> = { 0: '💳', 1: '🏦', 2: '💵', 3: '📈', 4: '🧾' };

// ---- Страница «Счета»: прямоугольные карточки в сетку 3 колонки + форма добавления/редактирования ----
export function AccountsPage({ onChanged }: { onChanged?: () => void }) {
  const [list, setList] = useState<AccountInfo[] | null>(null);
  const [err, setErr] = useState('');
  const [editing, setEditing] = useState<AccountInfo | null>(null); // null = нет формы, объект (id 0) = новая
  const [showArchived, setShowArchived] = useState(false);
  // Режим «вся семья»: показываем и управляем счетами всех участников семьи
  const [familyMode, setFamilyMode] = useState(true);
  const [inFamily, setInFamily] = useState(false);

  const load = () => {
    api.get<{ familyCode: string | null; members: FamilyMember[] }>('/api/family/members')
      .then(r => setInFamily(!!r.familyCode && r.members.length > 1))
      .catch(() => {});
    api.get<AccountInfo[]>(`/api/accounts?includeArchived=${showArchived}&family=${familyMode}`)
      .then(setList).catch(e => setErr(e.message));
  };
  useEffect(load, [showArchived, familyMode]);

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

  const visible = (list ?? []).filter(a => !a.isArchived);
  const total = visible.filter(a => a.type !== 1).reduce((s, a) => s + a.balance, 0);
  const debt = visible.filter(a => a.type === 1).reduce((s, a) => s + a.balance, 0);
  const showOwnerBadge = familyMode && inFamily && visible.some(a => (a.ownerName ?? '') !== (visible[0]?.ownerName ?? ''));

  const newAccount = () => setEditing({
    id: 0, name: '', type: 0 as AccountType, typeName: 'Дебетовая карта', balance: 0, startingBalance: 0,
    creditLimit: 0, cashbackRate: 0, cashbackAccrued: 0, color: '#2b6cb0', isArchived: false, available: 0, creditUsagePct: null,
  });

  return (
    <div className="card">
      <h3>💳 Счета{familyMode && inFamily ? ' · вся семья' : ''}</h3>
      {err && <div className="alert">{err}</div>}
      {!list ? <div className="muted">Загрузка…</div> : (
        <>
          <div className="metric-box" style={{ marginBottom: 10 }}>
            <div className="metric pos"><span>{fmt(total)}</span><small>на счетах{familyMode && inFamily ? ' семьи' : ''}</small></div>
            <div className={`metric ${debt > 0 ? 'neg' : ''}`}><span>{fmt(debt)}</span><small>долг по кредиткам</small></div>
          </div>
          {inFamily && (
            <label className="muted" style={{ display: 'flex', gap: 6, alignItems: 'center', marginBottom: 8 }}>
              <input type="checkbox" checked={familyMode} onChange={e => setFamilyMode(e.target.checked)} />
              управлять счетами всей семьи
            </label>
          )}
          {list.length === 0 && <div className="muted">Счетов пока нет — добавьте первый.</div>}
          <div className="account-grid">
            {list.map(a => (
              <div className="account-card" key={a.id} style={{ borderLeftColor: a.color }}>
                <div className="account-head">
                  <b>{TYPE_EMOJI[a.type]} {a.name}</b>
                  <span className="muted">{a.typeName}{a.isArchived ? ' · архив' : ''}</span>
                </div>
                {showOwnerBadge && <span className="badge">{a.ownerName || 'участник'}</span>}
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
                  <button className="btn small" title="Редактировать" onClick={() => setEditing({ ...a })}>✏️</button>
                  {a.type !== 1 && a.cashbackRate >= 0 && (
                    <button className="btn small" title="Зачислить кэшбэк" onClick={() => adjust(a, 'cashback')}>🎁</button>
                  )}
                  <button className="btn small" title="Корректировать баланс" onClick={() => adjust(a, 'adjust')}>±</button>
                  <button className="btn small" title={a.isArchived ? 'Вернуть из архива' : 'В архив'} onClick={() => archive(a)}>🗄</button>
                  <button className="btn danger-ghost small" onClick={() => del(a)}>✕</button>
                </div>
              </div>
            ))}
          </div>
          <label className="muted" style={{ display: 'flex', gap: 6, alignItems: 'center', marginTop: 8 }}>
            <input type="checkbox" checked={showArchived} onChange={e => setShowArchived(e.target.checked)} />
            показывать архивные
          </label>
        </>
      )}
      <button className="btn primary block" onClick={newAccount}>+ Добавить счёт</button>

      {editing && (
        <AccountForm account={editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); afterChange(); }} />
      )}
    </div>
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
    // Показываем активный пригласительный код сразу при открытии карточки (если он уже создан)
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
    if (inviteCode) navigator.clipboard?.writeText(inviteCode).then(() => setMsg('Код скопирован ✓')).catch(() => {});
  };

  // Я создатель семьи — блок «вступить по коду» не показываем (создатель уже в своей семье)
  const iAmOwner = !!members && members.some(m => m.isCreator);
  const showJoinForm = !iAmOwner;

  return (
    <div className="card">
      <h3>👨‍👩‍👧 Семья</h3>
      {err && <div className="alert">{err}</div>}
      {!members ? <div className="muted">Загрузка…</div> : members.length > 0 ? (
        <>
          <table>
            <thead><tr><th>Участник</th><th>Email</th></tr></thead>
            <tbody>
              {members.map(m => (
                <tr key={m.id}>
                  <td><b>{m.name}</b>{m.isOwner && <span className="muted"> · создатель</span>}</td>
                  <td className="muted">{m.email}</td>
                </tr>
              ))}
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
        </>
      )}
      {/* Модуль «вступить в семью по коду»: доступен всем, кто не является создателем семьи */}
      {showJoinForm && (
        <form onSubmit={join} style={{ marginTop: 10 }}>
          <div className="form-row">
            <label>У меня есть код
              <input value={joinCode} onChange={e => setJoinCode(e.target.value)} placeholder="напр. K7M2QX" maxLength={6}
                style={{ textTransform: 'uppercase' }} />
            </label>
            <button className="btn small" style={{ alignSelf: 'end' }}>Присоединиться</button>
          </div>
        </form>
      )}
      {iAmOwner && <div className="muted" style={{ marginTop: 8 }}>Вы создали эту семью — вступать по коду вам не нужно; приглашайте близких кнопкой выше.</div>}
      {msg && <div className="muted">{msg}</div>}
    </div>
  );
}
