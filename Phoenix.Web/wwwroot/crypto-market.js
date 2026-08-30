const faMarket = new Intl.NumberFormat('fa-IR');
const usdMarket = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 2 });
const escapeMarket = value => String(value ?? '').replace(/[&<>'"]/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[char]));
let marketAssets = [];

function renderMarket() {
  const query = document.querySelector('#marketSearch').value.trim().toUpperCase();
  const filter = document.querySelector('#marketFilter').value;
  const rows = marketAssets.filter(asset => {
    const matches = !query || asset.symbol.includes(query) || asset.baseSymbol.includes(query) || asset.name.toUpperCase().includes(query);
    return matches && (filter === 'all' || (filter === 'free' ? asset.activeCount === 0 : asset.activeCount > 0));
  });
  document.querySelector('#marketRows').innerHTML = rows.map((asset, index) => {
    const rank = asset.marketCapRank ? `#${faMarket.format(asset.marketCapRank)}` : `—`;
    const image = asset.image ? `<img src="${escapeMarket(asset.image)}" alt="">` : `<i>${escapeMarket(asset.baseSymbol.slice(0, 2))}</i>`;
    const longState = asset.activeLong ? `<b class="long">LONG ${faMarket.format(asset.activeLong)}</b>` : '<span>LONG ندارد</span>';
    const shortState = asset.activeShort ? `<b class="short">SHORT ${faMarket.format(asset.activeShort)}</b>` : '<span>SHORT ندارد</span>';
    return `<article class="market-row"><div class="asset"><em>${rank}</em>${image}<div><strong>${escapeMarket(asset.symbol)}</strong><small>${escapeMarket(asset.name)}</small></div></div><div class="cap">${asset.marketCap ? '$' + usdMarket.format(asset.marketCap) : 'نامشخص'}</div><div class="signal-state">${longState}${shortState}<small>${faMarket.format(asset.activeCount)} از ۲ فعال</small></div><a href="/analysis/signals?symbol=${encodeURIComponent(asset.symbol)}">باز کردن در Signal Lab</a></article>`;
  }).join('') || '<div class="market-loading">رمزارزی با این فیلتر پیدا نشد.</div>';
}

async function loadMarket() {
  const rows = document.querySelector('#marketRows');
  rows.innerHTML = '<div class="market-loading">در حال دریافت فهرست Bybit و اطلاعات مارکت‌کپ…</div>';
  document.querySelector('#marketMessage').textContent = '';
  try {
    const response = await fetch('/api/analysis/coins', { cache: 'no-store' });
    if (response.status === 401) return location.replace('/analysis/login');
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'اطلاعات بازار دریافت نشد.');
    marketAssets = data.assets || [];
    document.querySelector('#assetCount').textContent = faMarket.format(marketAssets.length);
    document.querySelector('#activeCount').textContent = faMarket.format(marketAssets.reduce((sum, asset) => sum + asset.activeCount, 0));
    document.querySelector('#freeCount').textContent = faMarket.format(marketAssets.filter(asset => asset.activeCount === 0).length);
    renderMarket();
  } catch (error) { rows.innerHTML = '<div class="market-loading error">دریافت اطلاعات ناموفق بود.</div>'; document.querySelector('#marketMessage').textContent = error.message; }
}

document.querySelector('#marketSearch').addEventListener('input', renderMarket);
document.querySelector('#marketFilter').addEventListener('change', renderMarket);
document.querySelector('#marketRefresh').addEventListener('click', loadMarket);
document.querySelector('#batchSize').value = localStorage.getItem('phoenix.signal.positionSizeUsdt') || '10';
const timedMode = document.querySelector('#batchTimedMode');
const batchCount = document.querySelector('#batchCount');
const batchDuration = document.querySelector('#batchDuration');
function renderBatchMode() {
  batchCount.disabled = timedMode.checked;
  batchDuration.disabled = !timedMode.checked;
  batchCount.closest('label').classList.toggle('is-disabled', timedMode.checked);
  batchDuration.closest('label').classList.toggle('is-disabled', !timedMode.checked);
  document.querySelector('#startBatch').textContent = timedMode.checked ? 'شروع جست‌وجوی زمان‌دار' : 'ایجاد سیگنال';
}
timedMode.addEventListener('change', renderBatchMode);
renderBatchMode();
document.querySelector('#startBatch').addEventListener('click', async () => {
  const button = document.querySelector('#startBatch'); button.disabled = true;
  try {
    const response = await fetch('/api/analysis/signal-batch', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ count: Number(batchCount.value), positionSizeUsdt: Number(document.querySelector('#batchSize').value), directionFilter: document.querySelector('#batchDirection').value, chartFilter: document.querySelector('#batchChart').value, timeframeFilter: document.querySelector('#batchTimeframe').value, timedMode: timedMode.checked, durationMinutes: Number(batchDuration.value) }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.error || 'شروع صف ناموفق بود.');
    localStorage.setItem('phoenix.signal.positionSizeUsdt', document.querySelector('#batchSize').value); renderBatch(data);
  } catch (error) { document.querySelector('#marketMessage').textContent = error.message; }
  finally { button.disabled = false; }
});
document.querySelector('#stopBatch').addEventListener('click', async () => {
  const button = document.querySelector('#stopBatch');
  button.disabled = true;
  try {
    const response = await fetch('/api/analysis/signal-batch/stop', { method: 'POST' });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'توقف ارسال ناموفق بود.');
    renderBatch(data);
  } catch (error) { document.querySelector('#marketMessage').textContent = error.message; }
  finally { await pollBatch(); }
});
function renderBatch(state) {
  const status = document.querySelector('#batchStatus');
  status.classList.toggle('running', state.running);
  const remainingMinutes = state.endsAtUtc ? Math.max(0, Math.ceil((new Date(state.endsAtUtc) - Date.now()) / 60000)) : 0;
  const progress = state.timedMode
    ? `تأیید ${faMarket.format(state.approved)} · پیشنهاد ${faMarket.format(state.proposed)} · باقی‌مانده حدود ${faMarket.format(remainingMinutes)} دقیقه`
    : `تأیید ${faMarket.format(state.approved)} از ${faMarket.format(state.target)}`;
  status.textContent = state.running ? `${state.message} · ${progress} · بررسی‌شده ${faMarket.format(state.checked)} · ردشده ${faMarket.format(state.rejected)}` : state.message;
  document.querySelector('#startBatch').disabled = state.running;
  document.querySelector('#stopBatch').disabled = !state.running;
}
async function pollBatch() { try { const response = await fetch('/api/analysis/signal-batch', { cache: 'no-store' }); if (response.ok) renderBatch(await response.json()); } catch {} }
setInterval(pollBatch, 4000); pollBatch();
const reportPrice = new Intl.NumberFormat('en-US', { maximumSignificantDigits: 12, useGrouping: false });
const dateInputValue = date => `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
const reportToday = new Date();
const reportFrom = new Date(reportToday); reportFrom.setDate(reportFrom.getDate() - 29);
document.querySelector('#resultsFrom').value = dateInputValue(reportFrom);
document.querySelector('#resultsTo').value = dateInputValue(reportToday);

function renderResults(data) {
  const summary = data.summary || {};
  const values = [summary.total, summary.entered, summary.expired, summary.target, summary.stopLoss, summary.riskFree];
  document.querySelectorAll('#resultsSummary b').forEach((node, index) => { node.textContent = faMarket.format(values[index] || 0); });
  const rows = data.details || [];
  document.querySelector('#resultsRows').innerHTML = rows.length ? rows.map(signal => {
    const isTarget = signal.outcome === 'Target';
    const result = isTarget ? 'تارگت' : 'استاپ‌لاس';
    const direction = signal.direction === 'Long' ? 'LONG' : 'SHORT';
    const ended = signal.completedAtUtc ? new Date(signal.completedAtUtc).toLocaleString('fa-IR') : '—';
    return `<article class="result-row"><strong>${escapeMarket(signal.symbol)}</strong><span>${direction}</span><b class="${isTarget ? 'target' : 'stop'}">${result}</b><span class="result-levels">ENTRY ${reportPrice.format(signal.entryPrice)} · TP ${reportPrice.format(signal.takeProfit)} · SL ${reportPrice.format(signal.stopLoss)}</span><span>${signal.leverage ? faMarket.format(signal.leverage) + '×' : '—'}</span><time>${ended}</time></article>`;
  }).join('') : '<div class="results-empty">در این بازه، نتیجه تارگت یا استاپ‌لاس ثبت نشده است.</div>';
}

document.querySelector('#loadResults').addEventListener('click', async () => {
  const button = document.querySelector('#loadResults');
  const message = document.querySelector('#resultsMessage');
  const fromValue = document.querySelector('#resultsFrom').value;
  const toValue = document.querySelector('#resultsTo').value;
  if (!fromValue || !toValue || fromValue > toValue) { message.textContent = 'بازه تاریخ معتبر نیست.'; return; }
  const from = new Date(`${fromValue}T00:00:00`);
  const to = new Date(`${toValue}T00:00:00`); to.setDate(to.getDate() + 1);
  button.disabled = true; message.textContent = '';
  document.querySelector('#resultsRows').innerHTML = '<div class="results-empty">در حال آماده‌سازی گزارش…</div>';
  try {
    const response = await fetch(`/api/analysis/results?from=${encodeURIComponent(from.toISOString())}&to=${encodeURIComponent(to.toISOString())}`, { cache: 'no-store' });
    if (response.status === 401) return location.replace('/analysis/login');
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'دریافت گزارش ناموفق بود.');
    renderResults(data);
  } catch (error) {
    message.textContent = error.message;
    document.querySelector('#resultsRows').innerHTML = '<div class="results-empty">گزارش دریافت نشد.</div>';
  } finally { button.disabled = false; }
});
document.querySelector('#analysisLogout').addEventListener('click', async () => { await fetch('/api/analysis/auth/logout', { method: 'POST' }); location.replace('/analysis/login'); });
loadMarket();
