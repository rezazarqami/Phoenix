const fa = new Intl.NumberFormat('fa-IR', { maximumFractionDigits: 4 });
const connection = document.querySelector('#connection');
const price = document.querySelector('#price');
const updated = document.querySelector('#updated');
const form = document.querySelector('#signalForm');
const message = document.querySelector('#message');
const panelKeyInput = form.querySelector('[name="panelKey"]');
panelKeyInput.value = localStorage.getItem('phoenixPanelKey') || '';

async function refreshStatus() {
  try {
    const response = await fetch('/api/status', { cache: 'no-store' });
    const data = await response.json();
    if (data.lastPrice) price.textContent = fa.format(data.lastPrice) + ' USDT';
    const connected = data.publicApiConnected;
    document.querySelector('#keyField').style.display = data.panelLocked ? 'block' : 'none';
    connection.textContent = connected ? (data.demoAuthenticated ? 'Bybit Demo متصل' : 'Bybit عمومی متصل') : 'خطای اتصال';
    connection.className = 'badge ' + (connected ? 'ok' : 'bad');
    updated.textContent = connected ? 'به‌روزرسانی خودکار هر ۱ ثانیه' : (data.error || 'ارتباط برقرار نشد');
  } catch { connection.textContent = 'سرور در دسترس نیست'; connection.className = 'badge bad'; }
}

async function refreshSignals() {
  const response = await fetch('/api/signals', { cache: 'no-store' });
  const signals = await response.json();
  document.querySelector('#count').textContent = fa.format(signals.length);
  document.querySelector('#orders').innerHTML = signals.length ? signals.map(s => `
    <article class="order"><strong>${s.symbol} · ${s.direction}</strong><span class="status">${s.status}</span>
    <small>کف ${fa.format(s.floor)} · سقف ${fa.format(s.ceiling)} · ${fa.format(s.positionSizeUsdt)} USDT</small></article>`).join('') : '<p class="empty">هنوز سیگنالی ثبت نشده است.</p>';
}

form.addEventListener('submit', async event => {
  event.preventDefault(); message.textContent = '';
  const button = form.querySelector('button'); button.disabled = true;
  const values = Object.fromEntries(new FormData(form));
  localStorage.setItem('phoenixPanelKey', values.panelKey || '');
  const response = await fetch('/api/signals', { method:'POST', headers:{'Content-Type':'application/json','X-Phoenix-Key':values.panelKey || ''}, body:JSON.stringify({
    symbol: values.symbol, direction: values.direction, ceiling:Number(values.ceiling), floor:Number(values.floor), positionSizeUsdt:Number(values.positionSizeUsdt)
  })});
  const data = await response.json();
  message.textContent = response.ok ? 'سیگنال با موفقیت در صف سرور ثبت شد.' : (data.error || 'ثبت سیگنال ناموفق بود.');
  button.disabled = false;
  if (response.ok) await refreshSignals();
});

if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js');
refreshStatus(); refreshSignals(); setInterval(refreshStatus, 1000); setInterval(refreshSignals, 5000);
