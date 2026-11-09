import { useEffect, useState, type FormEvent } from 'react';
import { api, AccountInfo, AccountRule, AccountRuleKind, Category, TxType } from './api';

const KIND_NAME: Record<number, string> = { 0: 'Автоподстановка', 1: 'Неснижаемый остаток', 2: 'Резерв' };
const KIND_TABS: number[] = [0, 1, 2];

export function AccountRulesCard() {
  const [kind, setKind] = useState<number>(0);
  const [rules, setRules] = useState<AccountRule[]>([]);
  const [accounts, setAccounts] = useState<AccountInfo[]>([]);
  const [cats, setCats] = useState<Category[]>([]);
  const [err, setErr] = useState('');

  const reload = async () => {
    const [r, c, a] = await Promise.all([
      api.get<AccountRule[]>(`/api/account-rules?kind=${kind}`).catch(() => [] as AccountRule[]),
      api.get<Category[]>('/api/categories').catch(() => [] as Category[]),
      api.get<AccountInfo[]>('/api/accounts').catch(() => [] as AccountInfo[]),
    ]);
    setRules(r); setCats(c); setAccounts(a);
  };
  useEffect(() => { reload(); }, [kind]);

  const save = async (r: AccountRule) => {
    const body: Record<string, unknown> = {
      name: r.name, kind: r.kind, isEnabled: r.isEnabled, priority: r.priority,
      accountId: r.accountId ?? null,
      txType: r.kind === 0 ? r.txType : null,
      matchCategoryId: r.kind === 0 ? r.matchCategoryId : null,
      commentContains: r.kind === 0 ? (r.commentContains ?? null) : null,
      amountMin: r.kind === 0 ? r.amountMin : null,
      amountMax: r.kind === 0 ? r.amountMax : null,
      setCategoryId: r.kind === 0 ? r.setCategoryId : null,
      threshold: r.kind === 1 || r.kind === 2 ? r.threshold : null,
      defaultReturnDays: r.kind === 2 ? r.defaultReturnDays : null,
      notifyInApp: r.notifyInApp,
    };
    if (r.id > 0) await api.put(`/api/account-rules/${r.id}`, body);
    else await api.post('/api/account-rules', body);
  };
  const toggle = async (r: AccountRule) => {
    const body: Record<string, unknown> = {
      name: r.name, kind: r.kind, isEnabled: !r.isEnabled, priority: r.priority,
      accountId: r.accountId ?? null, notifyInApp: r.notifyInApp,
      txType: r.kind === 0 ? r.txType : null, matchCategoryId: r.kind === 0 ? r.matchCategoryId : null,
      commentContains: r.kind === 0 ? (r.commentContains ?? null) : null, amountMin: r.kind === 0 ? r.amountMin : null,
      amountMax: r.kind === 0 ? r.amountMax : null, setCategoryId: r.kind === 0 ? r.setCategoryId : null,
      defaultReturnDays: r.kind === 2 ? r.defaultReturnDays : null,
      threshold: r.kind === 1 || r.kind === 2 ? r.threshold : null,
    };
    await api.put(`/api/account-rules/${r.id}`, body);
    reload();
  };
  const del = async (r: AccountRule) => { await api.del(`/api/account-rules/${r.id}`); reload(); };
  const applyToOld = async () => {
    const res = await api.post<{ matched: number; dryRun: boolean; samples?: unknown[] }>(
      '/api/account-rules/apply?dryRun=true', {});
    return res;
  };

  const createNew = () => {
    setKind(kind);
    // сбрасываем форму через ключ: RuleForm использует key={kind} у себя
  };

  return (
    <div className="card">
      <h3>Правила счетов</h3>
      <div className="tabs">
        {KIND_TABS.map(k => (
          <button key={k} type="button" className={`tab ${kind === k ? 'active' : ''}`} onClick={() => setKind(k)}>
            {KIND_NAME[k]}
          </button>
        ))}
      </div>
      {err && <div className="alert">{err}</div>}
      {rules.length === 0 && <div className="muted">Правил этого типа пока нет.</div>}
      {rules.length > 0 && (
        <table>
          <thead><tr><th>Правило</th><th>Условие / действие</th><th>Статус</th><th></th></tr></thead>
          <tbody>
            {rules.map(r => (
              <tr key={r.id}>
                <td><b>{r.name}</b></td>
                <td>{describeRule(r)}</td>
                <td>{r.isEnabled ? '✅ включено' : '⛔ выключено'}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button className="btn small" onClick={() => toggle(r)}>{r.isEnabled ? '⏸' : '▶'}</button>{' '}
                  <button className="btn danger-ghost small" onClick={() => del(r)}>✕</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {kind === 0 && (
        <button className="btn small" style={{ marginTop: 8 }} onClick={async () => {
          try { const res = await applyToOld(); alert(`Dry-run: затронуто операций ${res.matched}`); }
          catch (e: any) { alert(e.message); }
        }}>Применить к старым операциям (dry-run)</button>
      )}
      <div style={{ marginTop: 12 }}>
        <RuleForm key={kind} kind={kind} accounts={accounts} cats={cats} onSave={save} onSaved={reload} />
      </div>
      <button className="btn primary" onClick={() => { /* форма всегда в режиме «новое правило» */ }}>
        Новое правило
      </button>
    </div>
  );
}

function describeRule(r: AccountRule): string {
  if (r.kind === 0) {
    const parts: string[] = [];
    if (r.txType !== null) parts.push(r.txType === 0 ? 'доход' : 'расход');
    if (r.commentContains) parts.push(`комментарий содержит «${r.commentContains}»`);
    if (r.amountMin !== null || r.amountMax !== null) parts.push(`сумма ${r.amountMin ?? 0}–${r.amountMax ?? '∞'}`);
    if (r.matchCategoryId !== null) parts.push(`категория #${r.matchCategoryId}`);
    return `→ счёт «${r.accountName ?? r.accountId}» · ${parts.join(', ') || 'без условий'}`;
  }
  if (r.kind === 1) return `минимум ${r.threshold ?? 0} на счёт «${r.accountName ?? r.accountId}»`;
  return `резерв ${r.threshold ?? 0} · ${r.accountId === null ? 'общий резерв' : `счёт «${r.accountName}»`}`;
}

function RuleForm({ kind, accounts, cats, onSave, onSaved }: {
  kind: number; accounts: AccountInfo[]; cats: Category[];
  onSave: (r: AccountRule) => Promise<void>;
  onSaved: () => void;
}) {
  const isAuto = kind === 0;
  const isMin = kind === 1;
  const isRes = kind === 2;

  const blank = (): AccountRule => ({
    id: 0, name: '', kind: kind as AccountRuleKind, kindName: KIND_NAME[kind], isEnabled: true, priority: 100,
    accountId: null, accountName: null, txType: null, matchCategoryId: null, matchCategoryName: null,
    commentContains: null, amountMin: null, amountMax: null, setCategoryId: null, setCategoryName: null,
    threshold: null, defaultReturnDays: null, notifyInApp: true, createdAt: '',
  });
  const [form, setForm] = useState<AccountRule>(() => blank());
  const [err, setErr] = useState('');
  const [testing, setTesting] = useState(false);
  const [testRes, setTestRes] = useState('');

  const set = <K extends keyof AccountRule>(k: K, v: AccountRule[K]) => setForm(f => ({ ...f, [k]: v }));

  const submit = async (e: FormEvent) => {
    e.preventDefault(); setErr('');
    const rule: AccountRule = {
      ...form,
      name: form.name.trim(),
      accountId: (isAuto || isMin) ? (form.accountId ?? null) : (isRes ? (form.accountId ?? null) : null),
      threshold: (isMin || isRes) ? (Number(String(form.threshold ?? '')) || 0) : null,
      defaultReturnDays: isRes ? (Number(String(form.defaultReturnDays ?? '')) || 30) : null,
      amountMin: isAuto ? (Number(String(form.amountMin ?? '')) || null) : null,
      amountMax: isAuto ? (Number(String(form.amountMax ?? '')) || null) : null,
      priority: Number(form.priority) || 100,
    };
    try { await onSave(rule); onSaved(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const runTest = async () => {
    setTesting(true); setTestRes('');
    try {
      const res = await api.post<{ matched: boolean; ruleId?: number; accountId?: number | null; categoryId?: number | null }>(
        '/api/account-rules/test', {
          type: form.txType ?? 1, amount: Number(String(form.amountMin ?? '')) || 100,
          comment: form.commentContains ?? '', categoryId: form.matchCategoryId ?? null,
        });
      setTestRes(res.matched
        ? `✅ Совпало: правило #${res.ruleId} → счёт #${res.accountId}, категория #${res.categoryId ?? '—'}`
        : '❌ Совпадений не найдено');
    } catch (ex: any) { setTestRes(`Ошибка: ${ex.message}`); }
    finally { setTesting(false); }
  };

  const acctValue = kind === 2 ? (form.accountId ?? null) : (form.accountId ?? null);
  const acctOptions: { value: number | null; label: string }[] = [{ value: null, label: 'Общий резерв (все счета)' }];
  for (const a of accounts.filter(a => !a.isArchived)) acctOptions.push({ value: a.id, label: a.name });

  return (
    <form onSubmit={submit} className="account-form">
      <h4>Новое правило: {KIND_NAME[kind]}</h4>
      <label>Название
        <input value={form.name} onChange={e => set('name', e.target.value)} required placeholder="Напр.: Пятёрочка в основную" />
      </label>

      {(kind === 1 || kind === 2) && (
        <label>Счёт
          <select value={acctValue === null ? '' : String(acctValue)} onChange={e => set('accountId', e.target.value === '' ? null : Number(e.target.value))}>
            {kind === 2 && <option value="">{acctOptions[0].label}</option>}
            {kind !== 2 && <option value="">Выберите счёт</option>}
            {accounts.filter(a => !a.isArchived).map(a => (
              <option key={a.id} value={String(a.id)}>{a.name}</option>
            ))}
          </select>
        </label>
      )}

      {isAuto && (
        <>
          <div className="form-row">
            <label>Тип операции
              <select value={form.txType ?? 1} onChange={e => { const t = Number(e.target.value) as TxType; setForm(f => ({ ...f, txType: t, matchCategoryId: null })); }}>
                <option value={1}>Расход</option><option value={0}>Доход</option>
              </select>
            </label>
            <label>Приоритет
              <input inputMode="numeric" value={String(form.priority)} onChange={e => set('priority', Number(e.target.value) || 0)} />
            </label>
          </div>
          <label>Категория для сопоставления (необязательно)
            <select value={form.matchCategoryId ?? 0} onChange={e => set('matchCategoryId', Number(e.target.value) || null)}>
              <option value={0}>Любая</option>
              {cats.filter(c => c.type === form.txType).map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
          <label>Комментарий содержит
            <input value={form.commentContains ?? ''} onChange={e => set('commentContains', e.target.value || null)} placeholder="Напр.: Пятёрочка" />
          </label>
          <div className="form-row">
            <label>Сумма от, ₽
              <input inputMode="decimal" value={form.amountMin === null ? '' : String(form.amountMin)} onChange={e => set('amountMin', Number(e.target.value.replace(',', '.')) || null)} />
            </label>
            <label>Сумма до, ₽
              <input inputMode="decimal" value={form.amountMax === null ? '' : String(form.amountMax)} onChange={e => set('amountMax', Number(e.target.value.replace(',', '.')) || null)} />
            </label>
          </div>
          <label>Автокатегория (необязательно)
            <select value={form.setCategoryId ?? 0} onChange={e => set('setCategoryId', Number(e.target.value) || null)}>
              <option value={0}>Без автокатегории</option>
              {cats.filter(c => c.type === form.txType).map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>
        </>
      )}

      {(isMin || isRes) && (
        <div className="form-row">
          <label>{isMin ? 'Минимальный остаток, ₽' : 'Размер резерва, ₽'}
            <input inputMode="decimal" value={form.threshold === null ? '' : String(form.threshold)} onChange={e => set('threshold', Number(e.target.value.replace(',', '.')) || null)} required />
          </label>
          {isRes && (
            <label>Срок возврата, дней
              <input inputMode="numeric" value={String(form.defaultReturnDays ?? 30)} onChange={e => set('defaultReturnDays', Number(e.target.value) || null)} />
            </label>
          )}
        </div>
      )}

      <label className="muted" style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
        <input type="checkbox" checked={form.isEnabled} onChange={e => set('isEnabled', e.target.checked)} /> включить правило
      </label>
      {err && <div className="alert">{err}</div>}
      <div className="form-row" style={{ gap: 8 }}>
        <button className="btn primary" type="submit">Сохранить правило</button>
        <button className="btn" type="button" onClick={runTest} disabled={testing}>Проверить</button>
      </div>
      {testRes && <div className="muted" style={{ marginTop: 6 }}>{testRes}</div>}
    </form>
  );
}
