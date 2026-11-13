import { useEffect, useMemo, useRef, useState } from 'react';
import { useAuth } from './auth';
import {
  Summary, Category, Tx, TxType, AccountInfo, api, fmt, MONTHS_RU,
  ReserveInfo, ShareRow, AccountSummaryRow, FamilyAccountRow, MonthRow, ReceiptParseResult,
} from './api';
import { useToast } from './toasts';
import { Icon, type IconName } from './icons';
import { plural } from './plural';

// ============================================================================
// Вспомогательные функции
// ============================================================================
const pad2 = (n: number) => String(n).padStart(2, '0');

function greeting(): string {
  const h = new Date().getHours();
  if (h < 5) return 'Доброй ночи';
  if (h < 12) return 'Доброе утро';
  if (h < 18) return 'Добрый день';
  return 'Добрый вечер';
}

/** Полупрозрачный оттенок категорийного цвета для мягких иконок. */
function softColor(color?: string): string {
  if (!color) return 'var(--fill)';
  const c = color.trim();
  if (/^#[0-9a-f]{6}$/i.test(c)) return `${c}22`;
  if (/^#[0-9a-f]{3}$/i.test(c)) return `${c}22`;
  return 'var(--fill)';
}

const ACCOUNT_TONE: Record<number, string> = {
  0: 'debit', 1: 'credit', 2: 'cash', 3: 'deposit', 4: 'other',
};

const pctOf = (part: number, whole: number) =>
  whole > 0 ? Math.min(100, Math.max(0, (part / whole) * 100)) : 0;

// ============================================================================
// Дашборд
// ============================================================================
export function Dashboard({ onAdd, onFamilyTx }: { onAdd?: () => void; onFamilyTx?: (accountId: number) => void }) {
  const [sum, setSum] = useState<Summary | null>(null);
  const [recent, setRecent] = useState<Tx[]>([]);
  const [err, setErr] = useState('');
  const auth = useAuth();
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

  // Мягко подсвечиваем новые активные уведомления.
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

  const topShares = useMemo<ShareRow[]>(() => {
    if (!sum) return [];
    const sorted = [...sum.expenseShare].sort((a, b) => b.total - a.total);
    const top = sorted.slice(0, 5);
    const rest = sorted.slice(5);
    if (rest.length) {
      top.push({
        categoryId: -1,
        name: 'Прочее',
        color: '#8e8e93',
        total: rest.reduce((s, r) => s + r.total, 0),
      });
    }
    return top;
  }, [sum]);

  // Ровно 6 месяцев до текущего — пустые месяцы показываем нулевыми столбцами.
  const months = useMemo<MonthRow[]>(() => {
    if (!sum) return [];
    const byKey = new Map(sum.byMonth.map(m => [`${m.year}-${m.month}`, m]));
    const anchor = new Date(sum.year, sum.month - 1, 1);
    const out: MonthRow[] = [];
    for (let i = 5; i >= 0; i--) {
      const d = new Date(anchor.getFullYear(), anchor.getMonth() - i, 1);
      const key = `${d.getFullYear()}-${d.getMonth() + 1}`;
      out.push(byKey.get(key) ?? { year: d.getFullYear(), month: d.getMonth() + 1, income: 0, expense: 0 });
    }
    return out;
  }, [sum]);

  if (err) {
    return <div className="card"><div className="alert alert-danger">{err}</div></div>;
  }
  if (!sum) {
    return (
      <div className="card" style={{ padding: 42 }}>
        <div className="splash" style={{ minHeight: 180 }}>
          <div className="splash-mark"><Icon name="calculator" size={26} /></div>
          <span>Собираем ваш финансовый обзор…</span>
        </div>
      </div>
    );
  }

  const maxVal = Math.max(1, ...months.flatMap(m => [m.income, m.expense]));
  const totalExpense = topShares.reduce((s, x) => s + x.total, 0);
  const firstName = auth.user?.name?.trim().split(/\s+/)[0] ?? 'друг';
  const savings = Math.max(0, Math.min(100, sum.savingsRate));
  const accounts = sum.accounts ?? [];
  const familyAccounts = sum.familyAccounts ?? [];
  const monthName = MONTHS_RU[sum.month - 1];
  const balanceTone = sum.balance >= 0 ? 'pos' : 'neg';

  // Геометрия кольца сбережений
  const ringR = 52;
  const ringC = 2 * Math.PI * ringR;

  // Геометрия пончика
  const donutSize = 172;
  const donutStroke = 20;
  const donutR = (donutSize - donutStroke) / 2;
  const donutC = 2 * Math.PI * donutR;
  let donutOffset = 0;

  return (
    <>
      {/* ---------- Hero ---------- */}
      <section className="hero">
        <div className="hero-main">
          <span className="hero-eyebrow">
            <Icon name="sparkles" size={14} /> {greeting()}, {firstName}
          </span>
          <div className="hero-label">Общий баланс</div>
          <div className="hero-amount">{fmt(sum.totalBalance)}</div>
          <div className="hero-meta">
            <span className="hero-pill">
              <Icon name={sum.balance >= 0 ? 'trending-up' : 'trending-down'} size={14} />
              {sum.balance >= 0 ? '+' : '−'}{fmt(Math.abs(sum.balance))} за месяц
            </span>
            <span className="hero-sub">
              Доходы {fmt(sum.monthIncome)} · Расходы {fmt(sum.monthExpense)}
            </span>
          </div>
        </div>

        <div className="hero-side">
          <div className="hero-ring" title={`Норма сбережений: ${sum.savingsRate}%`}>
            <svg width="118" height="118" viewBox="0 0 118 118">
              <circle className="hero-ring-track" cx="59" cy="59" r={ringR} fill="none" strokeWidth="7" />
              <circle
                className="hero-ring-value"
                cx="59" cy="59" r={ringR} fill="none" strokeWidth="7"
                strokeDasharray={`${(savings / 100) * ringC} ${ringC}`}
              />
            </svg>
            <div className="hero-ring-text">
              <b>{sum.savingsRate}%</b>
              <span>сбережения</span>
            </div>
          </div>
        </div>

        <div className="hero-actions">
          <button type="button" className="hero-btn" onClick={onAdd}>
            <Icon name="plus" size={18} strokeWidth={2.4} /> Добавить операцию
          </button>
          <a className="hero-btn-ghost" href="#/journal">
            Все операции <Icon name="chevron-right" size={14} />
          </a>
        </div>
      </section>

      {/* ---------- KPI ---------- */}
      <div className="kpi-grid">
        <div className="kpi">
          <span className="kpi-ico tone-green"><Icon name="arrow-down-left" size={20} /></span>
          <div className="kpi-body">
            <div className="kpi-label">Доходы за месяц</div>
            <div className="kpi-value pos">{fmt(sum.monthIncome)}</div>
            <div className="kpi-hint">{monthName}</div>
          </div>
        </div>
        <div className="kpi">
          <span className="kpi-ico tone-red"><Icon name="arrow-up-right" size={20} /></span>
          <div className="kpi-body">
            <div className="kpi-label">Расходы за месяц</div>
            <div className="kpi-value">{fmt(sum.monthExpense)}</div>
            <div className="kpi-hint">
              {sum.monthIncome > 0 ? `${Math.round((sum.monthExpense / sum.monthIncome) * 100)}% от доходов` : 'нет доходов'}
            </div>
          </div>
        </div>
        <div className="kpi">
          <span className="kpi-ico tone-blue"><Icon name="wallet" size={20} /></span>
          <div className="kpi-body">
            <div className="kpi-label">Итог месяца</div>
            <div className={`kpi-value ${balanceTone}`}>{fmt(sum.balance)}</div>
            <div className="kpi-hint">{sum.balance >= 0 ? 'профицит' : 'перерасход'}</div>
          </div>
        </div>
        <div className="kpi">
          <span className="kpi-ico tone-orange"><Icon name="gift" size={20} /></span>
          <div className="kpi-body">
            <div className="kpi-label">Кэшбэк</div>
            <div className="kpi-value">{fmt(sum.cashbackTotal)}</div>
            <div className="kpi-hint">начислено по картам</div>
          </div>
        </div>
      </div>

      {/* ---------- Счета (Apple Wallet) ---------- */}
      <section className="section">
        <div className="section-head">
          <div>
            <div className="section-title">Мои счета</div>
            <div className="section-sub">
              {accounts.length > 0 ? `${accounts.length} ${plural(accounts.length, 'счёт', 'счёта', 'счетов')} · всегда под рукой` : 'Добавьте первый счёт, чтобы видеть общую картину'}
            </div>
          </div>
          <a className="card-link" href="#/accounts">
            Управлять <Icon name="chevron-right" size={14} />
          </a>
        </div>
        <div className="wallet-grid">
          {accounts.map(a => <WalletCard key={a.id} account={a} />)}
          {accounts.length === 0 && (
            <a className="wallet-add" href="#/accounts">
              <span><Icon name="plus" size={20} /></span>
              <span>Добавить счёт</span>
            </a>
          )}
        </div>
      </section>

      {/* ---------- Счета семьи ---------- */}
      {familyAccounts.length > 0 && (
        <section className="section">
          <div className="section-head">
            <div>
              <div className="section-title">Счета семьи</div>
              <div className="section-sub">
                {familyAccounts.length} {plural(familyAccounts.length, 'счёт', 'счёта', 'счетов')} ·
                общий баланс {fmt(sum.familyTotalBalance ?? 0)} ₽ · управление — во вкладке «Счета»
              </div>
            </div>
            <a className="card-link" href="#/accounts">
              Все счета <Icon name="chevron-right" size={14} />
            </a>
          </div>
          <div className="wallet-grid">
            {familyAccounts.map(a => (
              <FamilyWalletCard key={a.id} account={a} onTx={() => onFamilyTx?.(a.id)} />
            ))}
          </div>
        </section>
      )}

      {/* ---------- Динамика + структура ---------- */}
      <div className="grid-2">
        <div className="card">
          <div className="card-head">
            <div>
              <h3>Динамика бюджета</h3>
              <p className="card-sub">Доходы и расходы за последние 6 месяцев</p>
            </div>
            <div className="dyn-legend">
              <span className="dyn-legend-item"><i className="dot green" /> Доходы</span>
              <span className="dyn-legend-item"><i className="dot red" /> Расходы</span>
            </div>
          </div>
          {months.length === 0 ? (
            <EmptyState icon="budget" title="Пока нет данных" text="Добавьте первую операцию — и здесь появится динамика." />
          ) : (
            <div className="dyn-chart">
              {months.map(m => {
                const inH = Math.max(4, (m.income / maxVal) * 100);
                const outH = Math.max(4, (m.expense / maxVal) * 100);
                return (
                  <div className="dyn-col" key={`${m.year}-${m.month}`}>
                    <div className="dyn-bars">
                      <div className="dyn-bar income" style={{ height: `${inH}%` }} title={`${MONTHS_RU[m.month - 1]} · доходы ${fmt(m.income)}`} />
                      <div className="dyn-bar expense" style={{ height: `${outH}%` }} title={`${MONTHS_RU[m.month - 1]} · расходы ${fmt(m.expense)}`} />
                    </div>
                    <span className="dyn-month">{MONTHS_RU[m.month - 1].slice(0, 3)}</span>
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className="card">
          <div className="card-head">
            <div>
              <h3>Структура расходов</h3>
              <p className="card-sub">{monthName} · по категориям</p>
            </div>
          </div>
          {topShares.length === 0 || totalExpense <= 0 ? (
            <EmptyState icon="pie" title="Расходов пока нет" text="Структура появится, как только вы добавите траты за этот месяц." />
          ) : (
            <div className="donut-wrap">
              <div className="donut">
                <svg width={donutSize} height={donutSize} viewBox={`0 0 ${donutSize} ${donutSize}`}>
                  <circle className="donut-track" cx={donutSize / 2} cy={donutSize / 2} r={donutR} fill="none" strokeWidth={donutStroke} />
                  {topShares.map(s => {
                    const len = (s.total / totalExpense) * donutC;
                    const dash = Math.max(1.5, len - 2.5);
                    const el = (
                      <circle
                        key={s.categoryId}
                        className="donut-seg"
                        cx={donutSize / 2} cy={donutSize / 2} r={donutR}
                        fill="none" stroke={s.color} strokeWidth={donutStroke}
                        strokeDasharray={`${dash} ${donutC}`}
                        strokeDashoffset={-donutOffset}
                        strokeLinecap="round"
                      />
                    );
                    donutOffset += len;
                    return el;
                  })}
                </svg>
                <div className="donut-center">
                  <div className="donut-value">{fmt(totalExpense)}</div>
                  <div className="donut-label">всего расходов</div>
                </div>
              </div>
              <div className="donut-legend">
                {topShares.map(s => (
                  <div className="donut-row" key={s.categoryId}>
                    <i className="dot" style={{ background: s.color }} />
                    <span className="donut-name">{s.name}</span>
                    <span className="donut-amount">
                      {fmt(s.total)} <span className="donut-pct">{Math.round((s.total / totalExpense) * 100)}%</span>
                    </span>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>

      {/* ---------- Лимиты + рекомендации ---------- */}
      <div className="grid-2">
        <div className="card">
          <div className="card-head">
            <div>
              <h3>Лимиты категорий</h3>
              <p className="card-sub">{monthName} · расходы к лимиту</p>
            </div>
            <a className="card-link" href="#/budget">Настроить <Icon name="chevron-right" size={14} /></a>
          </div>
          {sum.limits.length === 0 ? (
            <EmptyState icon="shield" title="Лимиты не заданы" text="Задайте месячные лимиты — приложение подскажет, когда вы близки к перерасходу." />
          ) : (
            sum.limits.map(l => {
              const pct = pctOf(l.spent, l.limit);
              const over = l.spent > l.limit;
              const near = !over && pct >= 80;
              const tone = over ? 'var(--red)' : near ? 'var(--orange)' : 'var(--green)';
              return (
                <div className="limit-row" key={l.id}>
                  <div className="limit-head">
                    <span className="limit-name">
                      <i className="dot" style={{ background: l.color }} /> {l.name}
                    </span>
                    <span className={`limit-vals ${over ? 'over' : ''}`}>
                      {fmt(l.spent)} / {fmt(l.limit)}
                    </span>
                  </div>
                  <div className="progress">
                    <div style={{ width: `${pct}%`, background: tone }} />
                  </div>
                </div>
              );
            })
          )}
        </div>

        <div className="card">
          <div className="card-head">
            <div>
              <h3>Персональные рекомендации</h3>
              <p className="card-sub">Основано на ваших данных</p>
            </div>
            <span className="badge badge-muted"><Icon name="sparkles" size={12} /> {sum.recommendations.length}</span>
          </div>
          {sum.recommendations.length === 0 ? (
            <EmptyState icon="check" title="Всё под контролем" text="Пока нет советов — продолжайте вести учёт." />
          ) : (
            sum.recommendations.map((r, i) => (
              <div key={i} className={`rec rec-${r.level}`}>
                <span className="rec-icon">{r.icon}</span>
                <span>{r.text}</span>
              </div>
            ))
          )}
        </div>
      </div>

      {/* ---------- Цели ---------- */}
      {sum.goals.length > 0 && (
        <section className="section">
          <div className="section-head">
            <div>
              <div className="section-title">Финансовые цели</div>
              <div className="section-sub">Копилки и сроки исполнения</div>
            </div>
            <a className="card-link" href="#/goals">Все цели <Icon name="chevron-right" size={14} /></a>
          </div>
          <div className="grid-3">
            {sum.goals.slice(0, 3).map(g => (
              <div className="goal-card" key={g.id}>
                <div className="goal-top">
                  <div>
                    <div className="goal-name">{g.name}</div>
                    {g.deadline && <div className="goal-deadline">до {new Date(g.deadline).toLocaleDateString('ru-RU')}</div>}
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
                {g.perMonth !== null && g.monthsLeft !== null && (
                  <div className="goal-per">
                    {fmt(g.perMonth)}/мес · осталось {g.monthsLeft} мес.
                  </div>
                )}
              </div>
            ))}
            {sum.goals.length < 3 && (
              <a className="goal-add" href="#/goals">
                <span><Icon name="plus" size={20} /></span>
                <span>Новая цель</span>
              </a>
            )}
          </div>
        </section>
      )}

      {/* ---------- Уведомления по правилам ---------- */}
      {sum.alerts.length > 0 && (
        <div className="card">
          <div className="card-head">
            <div>
              <h3>Контроль счетов</h3>
              <p className="card-sub">Неснижаемые остатки, резервы и сроки возврата</p>
            </div>
            <span className="badge badge-warn"><Icon name="bell" size={12} /> {sum.alerts.length}</span>
          </div>
          {sum.alerts.map(a => (
            <div key={a.id} className={`alert-row ${a.level === 'danger' ? 'alert-danger' : 'alert-warn'}`}>
              <div className="alert-msg">{a.message}</div>
              <div className="muted">
                {a.kind === 'MinBalance' ? 'Неснижаемый остаток'
                  : a.kind === 'ReserveBroken' ? 'Нарушение резерва'
                    : a.kind === 'ReserveOverdue' ? 'Просроченный заём' : a.kind}
                {a.accountName ? ` · счёт «${a.accountName}»` : ''}
              </div>
              <div style={{ marginTop: 4 }}>
                <button className="btn small" onClick={() => dismissAlert(a.id)}>
                  <Icon name="check" size={14} /> Понятно
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* ---------- Резервы ---------- */}
      {sum.reserves.length > 0 && (
        <div className="card">
          <div className="card-head">
            <div>
              <h3>Резерв на экстренные нужды</h3>
              <p className="card-sub">Сколько можно потратить, не задев подушку безопасности</p>
            </div>
            <span className="badge badge-muted"><Icon name="shield" size={12} /> {sum.reserves.length}</span>
          </div>
          {sum.reserves.map(r => {
            const pct = r.threshold > 0 ? pctOf(r.free, r.threshold) : 0;
            const broken = r.free <= 0;
            return (
              <div className="limit-row" key={r.ruleId}>
                <div className="limit-head">
                  <span className="limit-name">
                    <Icon name="shield" size={14} /> {r.accountName ?? 'Общий резерв'} · {r.ruleName}
                  </span>
                  <span className={broken ? 'badge badge-danger' : 'badge badge-muted'}>
                    свободно {fmt(r.free)} из {fmt(r.threshold)}
                  </span>
                </div>
                <div className="progress">
                  <div className={broken ? 'over' : ''} style={{ width: `${pct}%`, background: broken ? undefined : 'var(--green)' }} />
                </div>
                <div className="muted" style={{ marginTop: 5 }}>
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
            <div style={{ marginTop: 6 }}>
              {sum.reserveLoans.map(l => (
                <div className="tx-item" key={l.id}>
                  <span className="tx-ico" style={{ color: 'var(--orange)', background: 'var(--orange-soft)' }}>
                    <Icon name="refresh" size={16} />
                  </span>
                  <div className="tx-main">
                    <div className="tx-title">Заём из резерва: {fmt(l.amount)}</div>
                    <div className="tx-sub">
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

      {/* ---------- Последние операции ---------- */}
      <div className="card">
        <div className="card-head">
          <div>
            <h3>Последние операции</h3>
            <p className="card-sub">{monthName} · {recent.length > 0 ? `${recent.length} ${plural(recent.length, 'запись', 'записи', 'записей')}` : 'список пуст'}</p>
          </div>
          <a className="card-link" href="#/journal">Открыть журнал <Icon name="chevron-right" size={14} /></a>
        </div>
        {recent.length === 0 ? (
          <EmptyState icon="operations" title="В этом месяце операций нет" text="Нажмите «Добавить операцию» — это займёт пару секунд." />
        ) : (
          <div className="tx-list">
            {recent.map(t => <TxRow key={t.id} tx={t} />)}
          </div>
        )}
      </div>
    </>
  );
}

// Карточка счёта члена семьи: та же стилистика, но с владельцем и быстрым действием.
function FamilyWalletCard({ account, onTx }: { account: FamilyAccountRow; onTx?: () => void }) {
  const tone = ACCOUNT_TONE[account.type] ?? 'other';
  return (
    <div className={`wallet wallet--${tone} wallet--family`}>
      <div className="wallet-top">
        <span className="wallet-type">
          <Icon name="user" size={13} />
          {account.ownerName || 'Семья'}
        </span>
        <span className="wallet-chip" aria-hidden="true" />
      </div>
      <div>
        <div className="wallet-name">{account.name}</div>
        <div className="wallet-balance">{fmt(account.balance)}</div>
      </div>
      <div className="wallet-foot">
        <span className="wallet-foot-item">{account.typeName}</span>
        {onTx && (
          <button className="btn small" onClick={onTx} title="Добавить операцию по счёту семьи">
            <Icon name="plus" size={12} /> Операция
          </button>
        )}
      </div>
    </div>
  );
}

// ============================================================================
// Карточка счёта в стиле Apple Wallet
// ============================================================================
function WalletCard({ account }: { account: AccountSummaryRow }) {
  const tone = ACCOUNT_TONE[account.type] ?? 'other';
  return (
    <div className={`wallet wallet--${tone}`}>
      <div className="wallet-top">
        <span className="wallet-type">
          <Icon name={account.type === 3 ? 'trending-up' : 'credit-card'} size={13} />
          {account.typeName}
        </span>
        <span className="wallet-chip" aria-hidden="true" />
      </div>
      <div>
        <div className="wallet-name">{account.name}</div>
        <div className="wallet-balance">{fmt(account.balance)}</div>
      </div>
      <div className="wallet-foot">
        {account.type === 1 && account.creditLimit > 0 ? (
          <span className="wallet-foot-item">лимит {fmt(account.creditLimit)}</span>
        ) : account.cashbackRate > 0 ? (
          <span className="wallet-foot-item"><Icon name="gift" size={12} /> кэшбэк {account.cashbackRate}%</span>
        ) : (
          <span className="wallet-foot-item">{account.type === 2 ? 'наличные' : 'активный счёт'}</span>
        )}
        <span className="wallet-foot-item" style={{ opacity: 0.7 }}>••••</span>
      </div>
    </div>
  );
}

// ============================================================================
// Строка операции
// ============================================================================
export function TxRow({ tx, onDelete }: { tx: Tx; onDelete?: (id: number) => void }) {
  const income = tx.type === 0;
  const color = tx.categoryColor || '#8e8e93';
  const initial = (tx.categoryName || '•').trim().charAt(0).toUpperCase();
  return (
    <div className="tx-item">
      <span className="tx-ico" style={{ color, background: softColor(color) }}>{initial}</span>
      <div className="tx-main">
        <div className="tx-title">
          {tx.categoryName}{tx.comment ? ` · ${tx.comment}` : ''}
        </div>
        <div className="tx-sub">
          {new Date(tx.date).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' })}
          {tx.accountName ? ` · ${tx.accountName}` : ''}
          {tx.isTransfer ? ' · перевод' : ''}
        </div>
      </div>
      <div className={`tx-sum ${income ? 'income' : 'expense'}`}>
        {income ? '+' : '−'}{fmt(tx.amount)}
      </div>
      {onDelete && (
        <button
          type="button"
          className="btn icon danger"
          style={{ width: 32, height: 32 }}
          title="Удалить операцию"
          aria-label="Удалить операцию"
          onClick={() => onDelete(tx.id)}
        >
          <Icon name="x" size={15} />
        </button>
      )}
    </div>
  );
}

// ============================================================================
// Быстрая операция
// ============================================================================
export function QuickTxModal({ categories, accounts = [], onClose, onSaved, presetAccountId }: {
  categories: Category[]; accounts?: AccountInfo[]; onClose: () => void; onSaved: () => void;
  presetAccountId?: number | null;
}) {
  const [type, setType] = useState<TxType>(1);
  const cats = categories.filter(c => c.type === type);
  const [categoryId, setCategoryId] = useState<number>(cats[0]?.id ?? 0);
  const [amount, setAmount] = useState('');
  const [date, setDate] = useState(new Date().toISOString().slice(0, 10));
  const [comment, setComment] = useState('');
  const [accountId, setAccountId] = useState<number | null>(presetAccountId ?? accounts[0]?.id ?? null);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState('');
  const [reserves, setReserves] = useState<ReserveInfo[]>([]);
  const [warn, setWarn] = useState<{ reserve: ReserveInfo; amount: number } | null>(null);

  const [importing, setImporting] = useState(false);
  const [importError, setImportError] = useState('');
  const [importResult, setImportResult] = useState<ReceiptParseResult | null>(null);
  const [previewUrl, setPreviewUrl] = useState<string | null>(null);
  const [manualOpen, setManualOpen] = useState(false);
  const [manualText, setManualText] = useState('');
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => () => {
    if (previewUrl) URL.revokeObjectURL(previewUrl);
  }, [previewUrl]);

  const applyParsed = (r: ReceiptParseResult) => {
    setImportResult(r);
    setType(r.type);
    if (r.amount !== null) setAmount(String(r.amount).replace('.', ','));
    if (r.date) setDate(r.date);
    if (r.comment) setComment(r.comment);
    if (r.categoryId !== null) setCategoryId(r.categoryId);
  };

  const parseReceiptFile = async (file: File) => {
    setImportError('');
    setImportResult(null);
    if (file.size > 10 * 1024 * 1024) {
      setImportError('Файл больше 10 МБ. Сожмите изображение и попробуйте снова.');
      return;
    }

    setPreviewUrl(URL.createObjectURL(file));
    setImporting(true);
    try {
      const form = new FormData();
      form.append('file', file);
      const parsed = await api.upload<ReceiptParseResult>('/api/import/receipt', form);
      applyParsed(parsed);
    } catch (ex: any) {
      setImportError(ex.message || 'Не удалось распознать изображение.');
      setManualOpen(true);
    } finally {
      setImporting(false);
      if (fileRef.current) fileRef.current.value = '';
    }
  };

  const parseManualText = async () => {
    setImporting(true);
    setImportError('');
    try {
      const parsed = await api.post<ReceiptParseResult>('/api/import/receipt/text', { text: manualText });
      applyParsed(parsed);
      setManualOpen(false);
    } catch (ex: any) {
      setImportError(ex.message || 'Не удалось разобрать текст.');
    } finally {
      setImporting(false);
    }
  };

  useEffect(() => {
    if (cats.length && !cats.some(c => c.id === categoryId)) setCategoryId(cats[0].id);
  }, [type, categories]);

  useEffect(() => {
    api.get<{ reserves: ReserveInfo[] }>('/api/budget/summary')
      .then(s => setReserves(s.reserves ?? []))
      .catch(() => {});
  }, []);

  const saveTxn = async () => {
    const acc = accounts.find(a => a.id === accountId) || null;
    const amountVal = Number(amount.replace(',', '.')) || 0;
    if (acc?.isFamily) {
      // Операция по счёту члена семьи: записывается владельцу счёта,
      // категория подбирается по названию среди его категорий.
      const catName = cats.find(c => c.id === categoryId)?.name ?? 'Прочее';
      await api.post(`/api/accounts/${acc.id}/tx`, {
        type, amount: amountVal, date, comment, categoryName: catName,
      });
    } else {
      await api.post('/api/transactions', {
        categoryId, type, amount: amountVal, date, comment, accountId,
      });
    }
    onSaved(); onClose();
  };

  const submit = async (e: React.FormEvent) => {
    e.preventDefault(); setErr(''); setBusy(true);
    const acc = accounts.find(a => a.id === accountId) || null;
    const amountVal = Number(amount.replace(',', '.')) || 0;
    const reserve = type === 1 && acc
      ? reserves.find(r => r.accountId === acc.id || r.accountId === null)
      : undefined;
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

  const addQuick = (n: number) => {
    const cur = Number(amount.replace(',', '.')) || 0;
    setAmount(String(cur + n));
  };

  return (
    <div className="sk-modal-back" onClick={onClose}>
      <form className="sk-modal" onClick={e => e.stopPropagation()} onSubmit={submit} role="dialog" aria-modal="true" aria-label="Новая операция">
        <div className="modal-head">
          <h3>Новая операция</h3>
          <button type="button" className="btn icon" onClick={onClose} aria-label="Закрыть">
            <Icon name="x" size={18} />
          </button>
        </div>

        <div className="segmented type-toggle">
          <button
            type="button"
            className={`segment ${type === 0 ? 'active income' : ''}`}
            onClick={() => setType(0)}
          >
            Доход
          </button>
          <button
            type="button"
            className={`segment ${type === 1 ? 'active expense' : ''}`}
            onClick={() => setType(1)}
          >
            Расход
          </button>
        </div>

        <div className="receipt-import">
          <input
            ref={fileRef}
            type="file"
            accept="image/*"
            capture="environment"
            hidden
            onChange={e => {
              const file = e.target.files?.[0];
              if (file) parseReceiptFile(file);
            }}
          />
          <button
            type="button"
            className="btn secondary"
            onClick={() => fileRef.current?.click()}
            disabled={importing}
          >
            <Icon name="image" size={16} /> {importing ? 'Распознаём…' : 'Скриншот или чек'}
          </button>
          <button
            type="button"
            className="btn ghost small"
            onClick={() => setManualOpen(v => !v)}
          >
            <Icon name="pencil" size={14} /> Вставить текст
          </button>
          {previewUrl && <img className="receipt-thumb" src={previewUrl} alt="Загруженный чек" />}
        </div>

        {importError && (
          <div className="alert alert-warn">
            <div>{importError}</div>
            <button
              type="button"
              className="btn small"
              style={{ marginTop: 8 }}
              onClick={() => setManualOpen(true)}
            >
              Вставить текст вручную
            </button>
          </div>
        )}

        {importResult && (
          <div className="receipt-preview">
            <div className="receipt-preview-head">
              <span className={`badge ${importResult.amount !== null ? 'badge-success' : 'badge-warn'}`}>
                Распознано · {Math.round(importResult.confidence * 100)}%
              </span>
              {importResult.warnings.map(w => (
                <span className="receipt-warning" key={w}>{w}</span>
              ))}
            </div>
            <div className="receipt-fields">
              <span>Сумма: <b>{importResult.amount !== null ? fmt(importResult.amount) : '—'}</b></span>
              <span>Дата: <b>{importResult.date.split('-').reverse().join('.')}</b></span>
              <span>Тип: <b>{importResult.typeName}</b></span>
              <span>Категория: <b>{importResult.categoryName ?? '—'}</b></span>
              {importResult.merchant && <span>Продавец: <b>{importResult.merchant}</b></span>}
            </div>
            <details>
              <summary>Распознанный текст</summary>
              <pre>{importResult.rawText}</pre>
            </details>
          </div>
        )}

        {manualOpen && (
          <div className="receipt-manual">
            <label>Вставьте текст чека
              <textarea
                value={manualText}
                onChange={e => setManualText(e.target.value)}
                placeholder={'Например:\nООО Пятёрочка\nИтого 1 250,00\n11.11.2026'}
              />
            </label>
            <button
              type="button"
              className="btn secondary small"
              disabled={importing || manualText.trim().length < 5}
              onClick={parseManualText}
            >
              <Icon name="sparkles" size={14} /> {importing ? 'Разбираем…' : 'Разобрать текст'}
            </button>
          </div>
        )}

        <label className="amount-field">
          <span>Сумма</span>
          <div className="amount-input">
            <input
              inputMode="decimal"
              value={amount}
              onChange={e => setAmount(e.target.value)}
              required
              autoFocus
              placeholder="0"
              aria-label="Сумма"
            />
            <span className="amount-cur">₽</span>
          </div>
        </label>

        <div className="quick-chips">
          {[100, 500, 1000, 5000].map(n => (
            <button key={n} type="button" className="quick-chip" onClick={() => addQuick(n)}>
              +{new Intl.NumberFormat('ru-RU').format(n)}
            </button>
          ))}
        </div>

        <label>Категория
          <select value={categoryId} onChange={e => setCategoryId(Number(e.target.value))}>
            {cats.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        </label>

        <div className="form-row">
          <label>Дата
            <input type="date" value={date} onChange={e => setDate(e.target.value)} required />
          </label>
          {accounts.length > 0 && (
            <label>Счёт
              <select value={accountId ?? 0} onChange={e => setAccountId(Number(e.target.value) || null)}>
                <option value={0}>Без счёта</option>
                {accounts.map(a => <option key={a.id} value={a.id}>{a.name}{a.isFamily ? ` · ${a.ownerName || 'семья'}` : ''}</option>)}
              </select>
            </label>
          )}
        </div>

        <label>Комментарий
          <input value={comment} onChange={e => setComment(e.target.value)} placeholder="Необязательно" />
        </label>

        {warn && (
          <div className={`alert-row ${warn.reserve.free <= 0 ? 'alert-danger' : 'alert-warn'}`}>
            <div className="alert-msg">
              ⚠️ Трата {fmt(warn.amount)} может затронуть резерв «{warn.reserve.ruleName}»
              (свободно {fmt(warn.reserve.free)}). Оформить это как заём из резерва?
            </div>
            <div className="form-row" style={{ gap: 8, marginTop: 8 }}>
              <button type="button" className="btn primary small" disabled={busy} onClick={takeLoan}>
                Оформить заём
              </button>
              <button type="button" className="btn small" disabled={busy} onClick={() => setWarn(null)}>
                Просто сохранить
              </button>
            </div>
          </div>
        )}

        {err && <div className="alert alert-danger">{err}</div>}

        <button className="btn primary block" style={{ marginTop: 6, height: 46 }} disabled={busy || !cats.length}>
          {busy ? 'Сохраняем…' : 'Сохранить операцию'}
        </button>
      </form>
    </div>
  );
}

// ============================================================================
// Журнал операций
// ============================================================================
export function Journal() {
  const now = new Date();
  const [ym, setYm] = useState(`${now.getFullYear()}-${pad2(now.getMonth() + 1)}`);
  const [txs, setTxs] = useState<Tx[] | null>(null);
  const [err, setErr] = useState('');
  const [q, setQ] = useState('');

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

  const shift = (delta: number) => {
    const [y, m] = ym.split('-').map(Number);
    const d = new Date(y, m - 1 + delta, 1);
    setYm(`${d.getFullYear()}-${pad2(d.getMonth() + 1)}`);
  };

  const list = (txs ?? []).filter(t => {
    if (!q.trim()) return true;
    const hay = `${t.categoryName ?? ''} ${t.comment ?? ''} ${t.accountName ?? ''}`.toLowerCase();
    return hay.includes(q.trim().toLowerCase());
  });
  const totalIn = list.filter(t => t.type === 0 && !t.isTransfer).reduce((s, t) => s + t.amount, 0);
  const totalOut = list.filter(t => t.type === 1 && !t.isTransfer).reduce((s, t) => s + t.amount, 0);
  const [yy, mm] = ym.split('-').map(Number);

  return (
    <div className="card">
      <div className="card-head" style={{ flexWrap: 'wrap' }}>
        <div>
          <h3>Журнал операций</h3>
          <p className="card-sub">{MONTHS_RU[mm - 1]} {yy} · всё, что было начислено и потрачено</p>
        </div>
        <div className="month-nav">
          <button type="button" className="btn icon" onClick={() => shift(-1)} aria-label="Предыдущий месяц" title="Предыдущий месяц">
            <Icon name="chevron-left" size={17} />
          </button>
          <input type="month" value={ym} onChange={e => setYm(e.target.value)} aria-label="Месяц" />
          <button type="button" className="btn icon" onClick={() => shift(1)} aria-label="Следующий месяц" title="Следующий месяц">
            <Icon name="chevron-right" size={17} />
          </button>
        </div>
      </div>

      <div className="form-row" style={{ marginBottom: 16 }}>
        <label style={{ flex: '1 1 260px', marginBottom: 0 }}>
          <div className="search-field">
            <Icon name="search" size={16} />
            <input value={q} onChange={e => setQ(e.target.value)} placeholder="Поиск по категории, счёту или комментарию" />
          </div>
        </label>
      </div>

      {err && <div className="alert alert-danger">{err}</div>}

      {!txs ? (
        <div className="empty"><div className="empty-text">Загрузка операций…</div></div>
      ) : (
        <>
          <div className="metric-box" style={{ marginBottom: 16 }}>
            <div className="metric pos"><span>+{fmt(totalIn)}</span><small>доходы</small></div>
            <div className="metric neg"><span>−{fmt(totalOut)}</span><small>расходы</small></div>
            <div className="metric"><span>{fmt(totalIn - totalOut)}</span><small>итог</small></div>
          </div>
          {list.length === 0 ? (
            <EmptyState
              icon="operations"
              title={q.trim() ? 'Ничего не нашлось' : 'Нет операций за этот месяц'}
              text={q.trim() ? 'Попробуйте изменить поисковый запрос.' : 'Нажмите «Добавить операцию», чтобы начать вести учёт.'}
            />
          ) : (
            <div className="tx-list">
              {list.map(t => <TxRow key={t.id} tx={t} onDelete={del} />)}
            </div>
          )}
        </>
      )}
    </div>
  );
}

// ============================================================================
// Мелкие компоненты
// ============================================================================
function EmptyState({ icon, title, text }: { icon: IconName; title: string; text: string }) {
  return (
    <div className="empty">
      <span className="empty-ico"><Icon name={icon} size={22} /></span>
      <div className="empty-title">{title}</div>
      <div className="empty-text">{text}</div>
    </div>
  );
}
