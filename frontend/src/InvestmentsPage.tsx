import { useEffect, useState } from 'react';
import {
  api, fmt, parseServerDate, BankInfo, PortfolioInfo, HoldingSummary, InvestmentsSummary, QuoteRefreshResult,
} from './api';
import { Icon } from './icons';
import { useDialog } from './dialogs';

const PRODUCTS = [
  { v: 0, label: 'Накопительный счёт' },
  { v: 1, label: 'Вклад' },
  { v: 2, label: 'Брокерский счёт' },
  { v: 3, label: 'ИИС' },
  { v: 4, label: 'Инвесткопилка' },
];

const STRATEGIES = [
  { v: 0, label: 'Всегда в плюсе — фонд «Денежный рынок»' },
  { v: 1, label: 'Сбалансированная — «Вечный портфель»' },
  { v: 2, label: 'Стабильные компании — «Крупнейшие компании РФ»' },
];

const ASSET_TYPES = [
  { v: 0, label: 'Деньги копилки' },
  { v: 1, label: 'Фонд / ETF' },
  { v: 2, label: 'Акция' },
  { v: 3, label: 'Облигация' },
  { v: 4, label: 'Золото' },
  { v: 9, label: 'Прочее' },
];

const productName = (v: number) => PRODUCTS.find(p => p.v === v)?.label ?? 'Прочее';
const assetTypeName = (v: number) => ASSET_TYPES.find(a => a.v === v)?.label ?? 'Прочее';
/** Подпись источника цены по коду QuoteSource (0 — вручную, 1 — MOEX ISS). */
const quoteSourceLabel = (v?: number | null) =>
  v === 1 ? 'MOEX ISS' : v === 0 ? 'вручную' : 'цена не обновлялась';

const fmtPrice = (n: number) =>
  new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 4 }).format(n);

const fmtTime = (iso: string | null | undefined) => iso
  ? parseServerDate(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' })
  : null;

const num = (s: string) => Number(s.replace(',', '.')) || 0;

/**
 * Мониторинг инвестиций и Инвесткопилки (Задача 6, пункты 1.0–1.3 плана).
 * Портфели привязаны к банку; по каждому считается стоимость, доходность
 * и сравнение с накопительным счётом (6 % / 8 % с подпиской Pro).
 * Котировки обновляются по тикерам через MOEX ISS; при недоступности
 * источника остаётся ручная цена, а в карточке видна ошибка.
 */
export function InvestmentsPage() {
  const dlg = useDialog();
  const [banks, setBanks] = useState<BankInfo[]>([]);
  const [portfolios, setPortfolios] = useState<PortfolioInfo[]>([]);
  const [summary, setSummary] = useState<InvestmentsSummary | null>(null);
  const [quotes, setQuotes] = useState<QuoteRefreshResult | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const [showNew, setShowNew] = useState(false);
  const [name, setName] = useState('');
  const [bankId, setBankId] = useState(0);
  const [product, setProduct] = useState(2);
  const [strategy, setStrategy] = useState(0);

  const [addTo, setAddTo] = useState<number | null>(null);
  const [editing, setEditing] = useState<HoldingSummary | null>(null);
  const [hName, setHName] = useState('');
  const [hTicker, setHTicker] = useState('');
  const [hType, setHType] = useState(1);
  const [hQty, setHQty] = useState('');
  const [hAvg, setHAvg] = useState('');
  const [hPrice, setHPrice] = useState('');

  const load = async () => {
    try {
      const [b, p, s] = await Promise.all([
        api.get<BankInfo[]>('/api/banks'),
        api.get<PortfolioInfo[]>('/api/investments'),
        api.get<InvestmentsSummary>('/api/investments/summary'),
      ]);
      setBanks(b);
      setPortfolios(p);
      setSummary(s);
      if (b.length && !bankId) setBankId(b[0].id);
    } catch (ex: any) { setErr(ex.message); }
  };

  const refreshQuotes = async (force: boolean) => {
    setRefreshing(true);
    setErr('');
    try {
      const r = await api.post<QuoteRefreshResult>(`/api/investments/refresh?force=${force}`);
      setQuotes(r);
      await load();
    } catch (ex: any) {
      setErr(ex.message);
    } finally {
      setRefreshing(false);
    }
  };

  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, []);

  // При загрузке страницы: если котировки устарели (или ещё не обновлялись) — обновим в фоне.
  useEffect(() => {
    if (!summary || summary.portfoliosCount === 0) return;
    const stale = !summary.quoteUpdatedAt
      || Date.now() - parseServerDate(summary.quoteUpdatedAt).getTime() > summary.autoRefreshMinutes * 60_000;
    if (stale) refreshQuotes(false);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [summary?.quoteUpdatedAt, summary?.portfoliosCount]);

  // Фоновое автообновление каждые N минут, пока страница открыта и вкладка видима.
  const intervalMin = summary?.autoRefreshMinutes ?? 5;
  useEffect(() => {
    const timer = window.setInterval(() => {
      if (document.visibilityState === 'visible') refreshQuotes(false);
    }, intervalMin * 60_000);
    return () => window.clearInterval(timer);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [intervalMin]);

  // первые банки подставляются автоматически
  useEffect(() => { if (banks.length && !bankId) setBankId(banks[0].id); }, [banks, bankId]);

  const createPortfolio = async (e: React.FormEvent) => {
    e.preventDefault();
    setErr(''); setBusy(true);
    try {
      await api.post('/api/investments', { name, bankId, product, strategy, currency: 'RUB' });
      setName(''); setShowNew(false);
      await load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const startAdd = (portfolioId: number) => {
    setAddTo(portfolioId);
    setEditing(null);
    setHName(''); setHTicker(''); setHType(1); setHQty(''); setHAvg(''); setHPrice('');
  };

  const startEdit = (portfolioId: number, h: HoldingSummary) => {
    setAddTo(portfolioId);
    setEditing(h);
    setHName(h.name); setHTicker(h.ticker); setHType(h.type);
    setHQty(String(h.quantity)); setHAvg(String(h.avgPrice)); setHPrice(String(h.currentPrice));
  };

  const closeForm = () => { setAddTo(null); setEditing(null); };

  const submitHolding = async (portfolioId: number, e: React.FormEvent) => {
    e.preventDefault();
    setErr(''); setBusy(true);
    const body = {
      name: hName, ticker: hTicker, assetType: hType,
      quantity: num(hQty), avgPrice: num(hAvg), currentPrice: num(hPrice), currency: 'RUB',
    };
    try {
      if (editing) {
        await api.put(`/api/investments/${portfolioId}/holdings/${editing.id}`, body);
      } else {
        await api.post(`/api/investments/${portfolioId}/holdings`, body);
      }
      closeForm();
      await load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const delPortfolio = async (id: number) => {
    if (!await dlg.confirm('Удалить портфель вместе с позициями?')) return;
    try { await api.del(`/api/investments/${id}`); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const delHolding = async (pid: number, hid: number) => {
    if (!await dlg.confirm('Удалить позицию?')) return;
    try { await api.del(`/api/investments/${pid}/holdings/${hid}`); closeForm(); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const quoteTime = fmtTime(quotes?.lastSuccessAt ?? summary?.quoteUpdatedAt);

  return (
    <div className="page">
      <div className="card">
        <div className="card-head">
          <div>
            <h4 style={{ margin: 0 }}>Инвестиции</h4>
            <p className="card-sub">Портфели по банкам, доходность и сравнение с накопительным счётом</p>
          </div>
          <button className="btn" onClick={() => refreshQuotes(true)} disabled={refreshing}>
            <Icon name="refresh" size={15} /> {refreshing ? 'Обновляем…' : 'Обновить'}
          </button>
        </div>

        {err && <div className="alert alert-danger">{err}</div>}

        {summary && (
          <div className="metric-row" style={{ marginTop: 10 }}>
            <div className="metric">
              <span>{fmt(summary.value)}</span>
              <small>стоимость активов</small>
            </div>
            <div className="metric">
              <span>{fmt(summary.invested)}</span>
              <small>вложено</small>
            </div>
            <div className="metric">
              <span style={{ color: summary.pnl >= 0 ? 'var(--green)' : 'var(--red)' }}>
                {summary.pnl >= 0 ? '+' : ''}{fmt(summary.pnl)}
              </span>
              <small>доход ({summary.pnlPct}%)</small>
            </div>
            <div className="metric">
              <span>{fmt(summary.savingsAt8)}</span>
              <small>копилка 8% за {summary.days} дн.</small>
            </div>
            <div className="metric">
              <span style={{ color: summary.riskPremium >= 0 ? 'var(--green)' : 'var(--red)' }}>
                {summary.riskPremium >= 0 ? '+' : ''}{fmt(summary.riskPremium)}
              </span>
              <small>цена риска vs копилка Pro</small>
            </div>
          </div>
        )}

        {summary && (
          <div className="muted" style={{ marginTop: 8, fontSize: 12 }}>
            Котировки: {summary.quoteSourceName} · {quoteTime ? `обновлено ${quoteTime}` : 'ещё не обновлялись'}
            {' '}· автообновление каждые {summary.autoRefreshMinutes} мин
            {quotes && quotes.failed > 0 ? ` · не обновлено позиций: ${quotes.failed}` : ''}
          </div>
        )}

        {summary?.quoteError && (
          <div className="alert alert-warn" style={{ marginTop: 8 }}>
            <div className="alert-msg">
              Часть котировок не обновлена: {summary.quoteError}. Для таких позиций остаётся
              ранее введённая цена.
            </div>
          </div>
        )}

        {summary && summary.invested > 0 && (
          <div className="alert alert-warn" style={{ marginTop: 10 }}>
            <div className="alert-msg">
              За {summary.days} дн. накопительный счёт Т-Банка дал бы {fmt(summary.savingsAt6)} ₽ (6 %)
              или {fmt(summary.savingsAt8)} ₽ (8 % с подпиской Pro). Ваши инвестиции: {fmt(summary.pnl)} ₽.
              Ориентир налога — {fmt(summary.taxEstimate)} ₽ (НДФЛ {summary.taxRate} %, удерживает брокер).
              Инвестиции не застрахованы по АСВ.
            </div>
          </div>
        )}
      </div>

      {showNew && (
        <div className="card">
          <form onSubmit={createPortfolio}>
            <div className="card-head">
              <div>
                <h4 style={{ margin: 0 }}>Новый портфель</h4>
                <p className="card-sub">Выберите банк, в котором открыт счёт</p>
              </div>
            </div>
            <div className="form-row">
              <label style={{ flex: '2 1 240px' }}>Название
                <input value={name} onChange={e => setName(e.target.value)} required placeholder="Напр.: Инвесткопилка" />
              </label>
              <label>Банк
                <select value={bankId} onChange={e => setBankId(Number(e.target.value))}>
                  {banks.map(b => <option key={b.id} value={b.id}>{b.name}</option>)}
                </select>
              </label>
              <label>Продукт
                <select value={product} onChange={e => setProduct(Number(e.target.value))}>
                  {PRODUCTS.map(p => <option key={p.v} value={p.v}>{p.label}</option>)}
                </select>
              </label>
              {product === 4 && (
                <label>Стратегия
                  <select value={strategy} onChange={e => setStrategy(Number(e.target.value))}>
                    {STRATEGIES.map(s => <option key={s.v} value={s.v}>{s.label}</option>)}
                  </select>
                </label>
              )}
            </div>
            <div className="action-row">
              <button className="btn primary" type="submit" disabled={busy}>
                <Icon name="check" size={16} /> Создать
              </button>
              <button type="button" className="btn secondary" onClick={() => setShowNew(false)}>Отмена</button>
            </div>
          </form>
        </div>
      )}

      {portfolios.length === 0 && !showNew && (
        <div className="card">
          <p className="muted" style={{ margin: 0 }}>
            Портфелей пока нет. Нажмите «Портфель», выберите банк и продукт —
            например, «Инвесткопилка» в Т-Банке — и добавьте позиции.
          </p>
        </div>
      )}

      {portfolios.map(p => (
        <div className="card" key={p.id}>
          <div className="card-head">
            <div>
              <h4 style={{ margin: 0 }}>{p.name}</h4>
              <p className="card-sub">
                {p.bankName} · {p.productName}
                {p.product === 4 ? ` · стратегия «${p.strategyName}»` : ''}
                {' '}· {p.currency}
              </p>
            </div>
            <button className="btn danger-ghost" title="Удалить портфель" onClick={() => delPortfolio(p.id)}>
              <Icon name="trash" size={15} />
            </button>
          </div>

          {p.product === 4 && (
            <div className="alert alert-warn" style={{ marginTop: 8 }}>
              <div className="alert-msg">
                {p.strategyHint} {p.riskNote}
              </div>
            </div>
          )}

          <div className="metric-row" style={{ marginTop: 10 }}>
            <div className="metric"><span>{fmt(p.value)}</span><small>стоимость</small></div>
            <div className="metric"><span>{fmt(p.invested)}</span><small>вложено</small></div>
            <div className="metric">
              <span style={{ color: p.pnl >= 0 ? 'var(--green)' : 'var(--red)' }}>
                {p.pnl >= 0 ? '+' : ''}{fmt(p.pnl)}
              </span>
              <small>доход ({p.pnlPct}%)</small>
            </div>
            <div className="metric"><span>{fmt(p.cashValue)}</span><small>деньги копилки</small></div>
            <div className="metric"><span>{fmt(p.savingsAt8)}</span><small>копилка 8% за {p.days} дн.</small></div>
          </div>

          <div style={{ marginTop: 12 }}>
            {p.holdings.length === 0 && (
              <p className="muted">Позиций нет — добавьте фонд, акцию или сумму денег копилки.</p>
            )}
            {p.holdings.map(h => (
              <div className="tx-item" key={h.id}>
                <span className="tx-ico" style={{ width: 30, height: 30, flex: '0 0 30px' }}>
                  <Icon name="sparkles" size={14} />
                </span>
                <div className="tx-main">
                  <div className="tx-title" style={{ fontSize: 13 }}>
                    {h.name} {h.ticker ? `· ${h.ticker}` : ''}
                  </div>
                  <div className="tx-sub">
                    {assetTypeName(h.type)} · {h.quantity} шт. × {fmtPrice(h.currentPrice)}
                    {' '}· {quoteSourceLabel(h.quoteSource)}
                    {h.quoteError ? ` · ${h.quoteError}` : ''}
                  </div>
                </div>
                <div className="holding-value">
                  <b>{fmt(h.value)}</b>
                  <span style={{ color: h.pnl >= 0 ? 'var(--green)' : 'var(--red)' }}>
                    {h.pnl >= 0 ? '+' : ''}{fmt(h.pnl)} ({h.pnlPct}%)
                  </span>
                </div>
                <button className="btn small" onClick={() => startEdit(p.id, h)}>
                  Изменить
                </button>
                <button className="btn danger-ghost" title="Удалить позицию" onClick={() => delHolding(p.id, h.id)}>
                  <Icon name="trash" size={14} />
                </button>
              </div>
            ))}
          </div>

          {addTo === p.id && (
            <form className="form-row" style={{ marginTop: 10 }} onSubmit={(e) => submitHolding(p.id, e)}>
              <label style={{ flex: '2 1 220px' }}>Название
                <input value={hName} onChange={e => setHName(e.target.value)} required placeholder="Вечный портфель" />
              </label>
              <label>Тикер / ISIN
                <input value={hTicker} onChange={e => setHTicker(e.target.value)} placeholder="FXGD" />
              </label>
              <label>Тип
                <select value={hType} onChange={e => setHType(Number(e.target.value))}>
                  {ASSET_TYPES.map(a => <option key={a.v} value={a.v}>{a.label}</option>)}
                </select>
              </label>
              <label>Количество
                <input inputMode="decimal" value={hQty} onChange={e => setHQty(e.target.value)} required placeholder="100" />
              </label>
              <label>Средняя цена
                <input inputMode="decimal" value={hAvg} onChange={e => setHAvg(e.target.value)} placeholder="10" />
              </label>
              <label>Текущая цена
                <input inputMode="decimal" value={hPrice} onChange={e => setHPrice(e.target.value)} placeholder="12" />
              </label>
              <button className="btn primary" type="submit" disabled={busy}>
                <Icon name="check" size={15} /> {editing ? 'Сохранить' : 'Добавить'}
              </button>
              <button type="button" className="btn secondary" onClick={closeForm}>Отмена</button>
            </form>
          )}

          {addTo !== p.id && (
            <button className="btn" style={{ marginTop: 10 }} onClick={() => startAdd(p.id)}>
              <Icon name="plus" size={14} /> Добавить позицию
            </button>
          )}
        </div>
      ))}
    </div>
  );
}
