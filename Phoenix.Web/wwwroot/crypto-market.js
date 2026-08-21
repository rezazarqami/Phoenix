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
document.querySelector('#startBatch').addEventListener('click', async () => {
  const button = document.querySelector('#startBatch'); button.disabled = true;
  try {
    const response = await fetch('/api/analysis/signal-batch', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ count: Number(document.querySelector('#batchCount').value), positionSizeUsdt: Number(document.querySelector('#batchSize').value), directionFilter: document.querySelector('#batchDirection').value, chartFilter: document.querySelector('#batchChart').value }) });
    const data = await response.json(); if (!response.ok) throw new Error(data.error || 'شروع صف ناموفق بود.');
    localStorage.setItem('phoenix.signal.positionSizeUsdt', document.querySelector('#batchSize').value); renderBatch(data);
  } catch (error) { document.querySelector('#marketMessage').textContent = error.message; }
  finally { button.disabled = false; }
});
function renderBatch(state) {
  const status = document.querySelector('#batchStatus');
  status.classList.toggle('running', state.running);
  status.textContent = state.running ? `${state.message} · تأیید ${faMarket.format(state.approved)} از ${faMarket.format(state.target)} · بررسی‌شده ${faMarket.format(state.checked)} · ردشده ${faMarket.format(state.rejected)}` : state.message;
  document.querySelector('#startBatch').disabled = state.running;
}
async function pollBatch() { try { const response = await fetch('/api/analysis/signal-batch', { cache: 'no-store' }); if (response.ok) renderBatch(await response.json()); } catch {} }
setInterval(pollBatch, 4000); pollBatch();
document.querySelector('#analysisLogout').addEventListener('click', async () => { await fetch('/api/analysis/auth/logout', { method: 'POST' }); location.replace('/analysis/login'); });
loadMarket();
