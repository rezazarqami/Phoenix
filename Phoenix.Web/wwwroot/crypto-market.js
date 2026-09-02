const faMarket = new Intl.NumberFormat('fa-IR');
const usdMarket = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 2 });
const escapeMarket = value => String(value ?? '').replace(/[&<>'"]/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[char]));
let marketAssets = [];

document.querySelector('.results-report').insertAdjacentHTML('beforebegin', `
<details class="results-report"><summary>آرشیو تأیید و رد پیشنهادها — خروجی برای تحلیل</summary>
<div class="results-body"><p class="results-note">همه پیشنهادهای جدید با عکس، کندل‌ها، تایم‌فریم و پاسخ شما ذخیره می‌شوند. بدون پاسخ، رد محسوب نمی‌شود. این آرشیو مستقل از نتایج معاملات است و فعلاً هیچ فیلتر هوش مصنوعی فعال نیست. خروجی فقط برای مدیر قابل دریافت است.</p>
<div class="results-filters"><label>از تاریخ<input id="reviewFrom" type="date"></label><label>تا تاریخ<input id="reviewTo" type="date"></label><button id="exportReviews" type="button">دریافت آرشیو ZIP</button></div>
<p id="reviewMessage" role="status" class="results-message"></p></div></details>`);
const reviewDate = date => `${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}`;
const reviewToday = new Date(), reviewStart = new Date();
reviewStart.setDate(reviewStart.getDate()-4);
document.querySelector('#reviewFrom').value = reviewDate(reviewStart);
document.querySelector('#reviewTo').value = reviewDate(reviewToday);
document.querySelector('#exportReviews').addEventListener('click', async () => {
  const button = document.querySelector('#exportReviews'), message = document.querySelector('#reviewMessage');
  const fromValue = document.querySelector('#reviewFrom').value, toValue = document.querySelector('#reviewTo').value;
  if (!fromValue || !toValue || fromValue > toValue) { message.textContent = 'بازه تاریخ معتبر نیست.'; return; }
  const from = new Date(`${fromValue}T00:00:00`), to = new Date(`${toValue}T00:00:00`);
  to.setDate(to.getDate()+1);
  button.disabled = true; message.textContent = 'در حال آماده‌سازی آرشیو…';
  try {
    const query = new URLSearchParams({from:from.toISOString(), to:to.toISOString()});
    const response = await fetch(`/api/analysis/reviews/export?${query}`, {cache:'no-store'});
    if (!response.ok) {
      if (response.status === 403) throw new Error('خروجی آرشیو فقط برای مدیر مجاز است.');
      const data = await response.json(); throw new Error(data.error || 'دریافت آرشیو ناموفق بود.');
    }
    const url = URL.createObjectURL(await response.blob()), link = document.createElement('a');
    link.href = url; link.download = `phoenix-reviews-${fromValue}-${toValue}.zip`;
    document.body.appendChild(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 30000);
    message.textContent = 'آرشیو دریافت شد. عکس‌ها در پوشه‌های Approved، Rejected و Unanswered هستند؛ مشخصات و کندل‌ها هم همراهشان است.';
  } catch(error) { message.textContent = error.message; }
  finally { button.disabled = false; }
});

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
    if (response.status === 401) return location.replace('/login');
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
  const label = key => ({ Long:'Long', Short:'Short', Target:'تارگت', StopLoss:'استاپ‌لاس', RiskFree:'ریسک‌فری', Expired:'اکسپایر', ExpiredNearEntry:'اکسپایر نزدیک ورود', 5:'5M', 15:'15M', 60:'1H', 240:'4H' })[key] || key;
  const group = (title, items) => `<section><strong>${title}</strong><div>${(items || []).map(item => `<span>${escapeMarket(label(item.key))}<b>${faMarket.format(item.count)}</b></span>`).join('') || '<small>داده‌ای نیست</small>'}</div></section>`;
  document.querySelector('#resultsBreakdown').innerHTML = group('بر اساس جهت', data.byDirection) + group('بر اساس تایم‌فریم', data.byTimeframe) + group('بر اساس نتیجه', data.byOutcome);
  const rows = data.details || [];
  document.querySelector('#resultsRows').innerHTML = rows.length ? rows.map(signal => {
    const result = signal.outcome === 'Target' ? 'تارگت' : signal.outcome === 'StopLoss' ? 'استاپ‌لاس' : signal.outcome === 'RiskFree' ? 'ریسک‌فری' : 'اکسپایر نزدیک ورود';
    const resultClass = signal.outcome === 'Target' ? 'target' : signal.outcome === 'StopLoss' ? 'stop' : 'neutral';
    const direction = signal.direction === 'Long' ? 'LONG' : 'SHORT';
    const ended = signal.completedAtUtc ? new Date(signal.completedAtUtc).toLocaleString('fa-IR') : '—';
    const image = signal.imageUrl ? `<a class="result-image" href="${signal.imageUrl}" target="_blank"><img src="${signal.imageUrl}" alt="نمودار ${escapeMarket(signal.symbol)}"><span>مشاهده تصویر</span></a>` : '';
    return `<article class="result-row"><div class="result-symbol">${image}<strong>${escapeMarket(signal.symbol)}</strong></div><span>${direction}</span><b class="${resultClass}">${result}</b><span class="result-levels"><b>${escapeMarket(label(signal.timeframe || 'نامشخص'))} · ${escapeMarket(signal.chartMode || '')}</b>ENTRY ${reportPrice.format(signal.entryPrice)} · TP ${reportPrice.format(signal.takeProfit)} · SL ${reportPrice.format(signal.stopLoss)}</span><span>${signal.leverage ? faMarket.format(signal.leverage) + '×' : '—'}</span><time>${ended}</time></article>`;
  }).join('') : '<div class="results-empty">در این فیلتر نتیجه‌ای برای نمایش وجود ندارد.</div>';
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
    const query = new URLSearchParams({ from: from.toISOString(), to: to.toISOString(), direction: document.querySelector('#resultsDirection').value, outcome: document.querySelector('#resultsOutcome').value, timeframe: document.querySelector('#resultsTimeframe').value });
    const response = await fetch(`/api/analysis/results?${query}`, { cache: 'no-store' });
    if (response.status === 401) return location.replace('/login');
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'دریافت گزارش ناموفق بود.');
    renderResults(data);
  } catch (error) {
    message.textContent = error.message;
    document.querySelector('#resultsRows').innerHTML = '<div class="results-empty">گزارش دریافت نشد.</div>';
  } finally { button.disabled = false; }
});
document.querySelector('#analysisLogout').addEventListener('click', async () => { await fetch('/api/analysis/auth/logout', { method: 'POST' }); location.replace('/login'); });
loadMarket();
