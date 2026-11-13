import { useEffect, useState } from 'react';
import {
  api, fmt, BankInfo, PortfolioInfo, HoldingInfo, InvestmentsSummary,
} from './api';
import { Icon } from './icons';

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

/**
 * Мониторинг инвестиций и Инвесткопилки (Задача 6).
 * Портфели привязаны к банку; по каждому считается стоимость, доходность
 * и сравнение с накопительным счётом (6 % / 8 % с подпиской Pro).
 */
export function InvestmentsPage() {
  const [banks, setBanks] = useState<BankInfo[]>([]);
  const [portfolios, setPortfolios] = useState<PortfolioInfo[]>([]);
  const [summary, setSummary] = useState<InvestmentsSummary | null>(null);
  const [err, setErr] = useState('');
  const [busy, setBusy] = useState(false);

  const [showNew, setShowNew] = useState(false);
  const [name, setName] = useState('');
  const [bankId, setBankId] = useState(0);
  const [product, setProduct] = useState(2);
  const [strategy, setStrategy] = useState(0);

  const [addTo, setAddTo] = useState<number | null>(null);
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

  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, []);

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

  const addHolding = async (portfolioId: number, e: React.FormEvent) => {
    e.preventDefault();
    setErr(''); setBusy(true);
    try {
      await api.post(`/api/investments/${portfolioId}/holdings`, {
        name: hName, ticker: hTicker, assetType: hType,
        quantity: Number(hQty.replace(',', '.')) || 0,
        avgPrice: Number(hAvg.replace(',', '.')) || 0,
        currentPrice: Number(hPrice.replace(',', '.')) || 0,
        currency: 'RUB',
      });
      setHName(''); setHTicker(''); setHQty(''); setHAvg(''); setHPrice('');
      setAddTo(null);
      await load();
    } catch (ex: any) { setErr(ex.message); } finally { setBusy(false); }
  };

  const updatePrice = async (portfolioId: number, h: HoldingInfo, price: string) => {
    const value = Number(price.replace(',', '.')) || 0;
    setErr('');
    try {
      await api.put(`/api/investments/${portfolioId}/holdings/${h.id}`, {
        name: h.name, ticker: h.ticker, assetType: h.assetType,
        quantity: h.quantity, avgPrice: h.avgPrice, currentPrice: value, currency: h.currency,
      });
      await load();
    } catch (ex: any) { setErr(ex.message); }
  };

  const delPortfolio = async (id: number) => {
    if (!confirm('Удалить портфель вместе с позициями?')) return;
    try { await api.del(`/api/investments/${id}`); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  const delHolding = async (pid: number, hid: number) => {
    if (!confirm('Удалить позицию?')) return;
    try { await api.del(`/api/investments/${pid}/holdings/${hid}`); await load(); }
    catch (ex: any) { setErr(ex.message); }
  };

  return (
    <div className="page">
      <div className="card">
        <div className="card-head">
          <div>
            <h4 style={{ margin: 0 }}>Инвестиции</h4>
            <p className="card-sub">Портфели по банкам, доходность и сравнение с накопительным счётом</p>
          </div>
          <button className="btn primary" onClick={() => setShowNew(v => !v)}>
            <Icon name="plus" size={15} /> Портфель
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
              <div className="tx-item" key={h.name + h.ticker}>
                <span className="tx-ico" style={{ width: 30, height: 30, flex: '0 0 30px' }}>
                  <Icon name="sparkles" size={14} />
                </span>
                <div className="tx-main">
                  <div className="tx-title" style={{ fontSize: 12.5 }}>
                    {h.name} {h.ticker ? `· ${h.ticker}` : ''}
                  </div>
                  <div className="tx-sub">
                    {assetTypeName(h.type)} · {h.quantity} шт. · средняя {fmt(h.avgPrice)} · текущая {fmt(h.currentPrice)}
                    {' '}· вложено {fmt(h.invested)} · стоимость {fmt(h.value)}
                    {' '}· {h.pnl >= 0 ? '+' : ''}{fmt(h.pnl)} ({h.pnlPct}%)
                  </div>
                </div>
                <button className="btn small" onClick={() => setAddTo(addTo === p.id ? null : p.id)}>
                  {addTo === p.id ? 'Скрыть' : 'Изменить'}
                </button>
                <button className="btn danger-ghost" title="Удалить позицию" onClick={() => delHolding(p.id, h.id)}>
                  <Icon name="trash" size={14} />
                </button>
              </div>
            ))}
          </div>

          {addTo === p.id && (
            <form className="form-row" style={{ marginTop: 10 }} onSubmit={(e) => addHolding(p.id, e)}>
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
                <Icon name="check" size={15} /> Добавить
              </button>
            </form>
          )}

          {!p.holdings.length && addTo !== p.id && (
            <button className="btn" style={{ marginTop: 10 }} onClick={() => setAddTo(p.id)}>
              <Icon name="plus" size={14} /> Добавить позицию
            </button>
          )}
        </div>
      ))}
    </div>
  );
}
