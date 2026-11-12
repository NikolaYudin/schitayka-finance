import { spawn } from 'node:child_process';
import { writeFileSync, rmSync, mkdirSync } from 'node:fs';
import path from 'node:path';

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const BASE = process.env.BASE || 'http://localhost:5028';
const TOKEN = process.env.SK_TOKEN || '';
const PORT = Number(process.env.CDP_PORT || 9601);
const OUT = path.resolve(process.env.OUT || 'shot.png');
const W = Number(process.env.W || 1440);
const H = Number(process.env.H || 1400);
const ROUTE = process.env.ROUTE || '';
const CLICK = process.env.CLICK || '';
const FULL = process.env.FULL !== '0';
const THEME = process.env.THEME || 'light';

const profile = path.resolve('.chrome-shot');
rmSync(profile, { recursive: true, force: true });
mkdirSync(profile, { recursive: true });

const child = spawn(CHROME, [
  '--headless=new', '--disable-gpu', '--no-sandbox', '--hide-scrollbars',
  `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  `--window-size=${W},${H}`, 'about:blank',
], { stdio: 'ignore' });

const sleep = (ms) => new Promise(r => setTimeout(r, ms));
let page;
for (let i = 0; i < 80; i++) {
  try {
    const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
    page = list.find(t => t.type === 'page');
    if (page?.webSocketDebuggerUrl) break;
  } catch { /* wait */ }
  await sleep(150);
}
if (!page) throw new Error('CDP not ready');

const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
let id = 0;
const pending = new Map();
const problems = [];
ws.onmessage = (e) => {
  const m = JSON.parse(e.data);
  if (m.id && pending.has(m.id)) {
    const p = pending.get(m.id); pending.delete(m.id);
    m.error ? p.reject(new Error(m.error.message)) : p.resolve(m.result);
    return;
  }
  if (m.method === 'Runtime.exceptionThrown') {
    problems.push(`JS: ${m.params.exceptionDetails.text}`);
  }
};
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const i = ++id; pending.set(i, { resolve, reject });
  ws.send(JSON.stringify({ id: i, method, params }));
});
const evalJs = async (expression) => {
  const r = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  return r.result?.value;
};

await send('Page.enable');
await send('Runtime.enable');
await send('Emulation.setDeviceMetricsOverride', { width: W, height: H, deviceScaleFactor: 1, mobile: false });
await send('Page.navigate', { url: `${BASE}/#/auth-success#token=${encodeURIComponent(TOKEN)}` });
await sleep(3000);
await evalJs(`localStorage.setItem('sk_theme', ${JSON.stringify(THEME)}); location.reload(); true`);
await sleep(3000);
if (ROUTE) {
  await evalJs(`location.hash = '#/${ROUTE}'; true`);
  await sleep(2000);
}
if (CLICK) {
  await evalJs(`document.querySelector(${JSON.stringify(CLICK)})?.click(); true`);
  await sleep(1500);
}
const EXTRA = process.env.EXTRA || '';
if (EXTRA) {
  await evalJs(EXTRA);
  await sleep(1500);
}
if (process.env.TYPE) {
  const spec = JSON.parse(process.env.TYPE);
  await evalJs(`(() => { const el = document.querySelector(${JSON.stringify(spec.selector)}); el?.focus(); return true; })()`);
  await send('Input.insertText', { text: spec.text });
  await sleep(400);
}
if (process.env.CLICK2) {
  await evalJs(`document.querySelector(${JSON.stringify(process.env.CLICK2)})?.click(); true`);
  await sleep(1800);
}

if (FULL) {
  await evalJs(`(() => {
    const s = document.querySelector('.shell');
    const p = document.querySelector('.page-scroll');
    if (s) { s.style.height = 'auto'; s.style.minHeight = '0'; }
    if (p) { p.style.overflow = 'visible'; p.style.height = 'auto'; p.style.flex = 'none'; }
    const a = document.querySelector('.app');
    if (a) { a.style.minHeight = '0'; a.style.height = 'auto'; }
    return true;
  })()`);
  await sleep(400);
}

const metrics = await send('Page.getLayoutMetrics');
const content = metrics.cssContentSize || metrics.contentSize;
const h = FULL ? Math.min(Math.max(H, Math.ceil(content.height)), 6000) : H;
await send('Emulation.setDeviceMetricsOverride', { width: W, height: h, deviceScaleFactor: 1, mobile: false });
await sleep(500);
const shot = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true });
writeFileSync(OUT, Buffer.from(shot.data, 'base64'));
console.log(`${OUT} ${W}x${h}`);
if (process.env.DUMP) {
  const html = await evalJs(`document.querySelector(${JSON.stringify(process.env.DUMP)})?.outerHTML || 'NOT FOUND'`);
  console.log(html.slice(0, 6000));
}
if (problems.length) console.log('PROBLEMS\n' + problems.join('\n'));
ws.close();
spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
process.exit(0);
