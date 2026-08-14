const fa = new Intl.NumberFormat('fa-IR', { maximumFractionDigits: 4 });
const connection = document.querySelector('#connection');
const price = document.querySelector('#price');
const updated = document.querySelector('#updated');
const form = document.querySelector('#signalForm');
const message = document.querySelector('#message');
const panelKeyInput = form.querySelector('[name="panelKey"]');
const symbolPicker = document.querySelector('#symbolPicker');
const symbolSearch = document.querySelector('#symbolSearch');
const symbolValue = form.querySelector('[name="symbol"]');
const symbolMenu = document.querySelector('#symbolMenu');
const symbolOptions = document.querySelector('#symbolOptions');
const symbolEmpty = document.querySelector('#symbolEmpty');
const symbolCount = document.querySelector('#symbolCount');
const securityDialog = document.querySelector('#securityDialog');
const securityForm = document.querySelector('#securityForm');
const securityMessage = document.querySelector('#securityMessage');
let instruments = [];

panelKeyInput.value = localStorage.getItem('phoenixPanelKey') || '';

async function refreshStatus() {
  try {
    const response = await fetch('/api/status', { cache: 'no-store' });
    const data = await response.json();
    if (data.lastPrice) price.textContent = fa.format(data.lastPrice) + ' USDT';
    const connected = data.publicApiConnected;
    document.querySelector('#keyField').style.display = data.panelLocked ? 'block' : 'none';
    document.querySelector('#modeNote').innerHTML = data.tradingEnabled
      ? '<i>✓</i><span>موتور Bybit Demo فعال است؛ سفارش پس از رسیدن قیمت به نقطه ورود ارسال می‌شود.</span>'
      : '<i>!</i><span>موتور ارسال سفارش خاموش است؛ سیگنال فقط در صف دائمی ذخیره می‌شود.</span>';
    connection.innerHTML = '<i></i>' + (connected ? (data.demoAuthenticated ? 'Bybit Demo متصل' : 'Bybit عمومی متصل') : 'خطای اتصال');
    connection.className = 'badge ' + (connected ? 'ok' : 'bad');
    updated.textContent = connected ? 'به‌روزرسانی خودکار هر ۱ ثانیه' : (data.error || 'ارتباط برقرار نشد');
  } catch {
    connection.innerHTML = '<i></i>سرور در دسترس نیست';
    connection.className = 'badge bad';
  }
}

async function loadInstruments() {
  try {
    const response = await fetch('/api/instruments', { cache: 'no-store' });
    if (!response.ok) throw new Error();
    const data = await response.json();
    instruments = data.symbols || [];
    symbolCount.textContent = fa.format(instruments.length) + ' نماد';
    const initial = instruments.includes('BTCUSDT') ? 'BTCUSDT' : instruments[0];
    if (initial) selectSymbol(initial, false);
    renderSymbols('');
  } catch {
    symbolCount.textContent = 'خطا در دریافت';
    document.querySelector('#symbolHint').textContent = 'دریافت نمادها از Bybit ناموفق بود؛ چند لحظه دیگر صفحه را تازه کنید.';
  }
}

function renderSymbols(query) {
  const normalized = query.trim().toUpperCase();
  const filtered = instruments.filter(symbol => symbol.startsWith(normalized)).slice(0, 250);
  symbolOptions.innerHTML = filtered.map(symbol => {
    const quote = symbol.endsWith('USDT') ? 'USDT Perpetual' : symbol.endsWith('USDC') ? 'USDC Perpetual' : 'Linear Futures';
    return `<button type="button" class="symbol-option${symbol === symbolValue.value ? ' active' : ''}" role="option" data-symbol="${symbol}"><span><em></em>${quote}</span><b>${symbol}</b></button>`;
  }).join('');
  symbolEmpty.hidden = filtered.length > 0;
  symbolCount.textContent = fa.format(filtered.length) + (normalized ? ' نتیجه' : ' نماد');
}

function openSymbolMenu() {
  symbolMenu.hidden = false;
  symbolSearch.setAttribute('aria-expanded', 'true');
  renderSymbols(symbolSearch.value === symbolValue.value ? '' : symbolSearch.value);
}

function closeSymbolMenu() {
  symbolMenu.hidden = true;
  symbolSearch.setAttribute('aria-expanded', 'false');
  if (!instruments.includes(symbolSearch.value.toUpperCase())) symbolSearch.value = symbolValue.value;
}

function selectSymbol(symbol, close = true) {
  symbolValue.value = symbol;
  symbolSearch.value = symbol;
  symbolPicker.classList.remove('invalid');
  if (close) closeSymbolMenu();
}

symbolSearch.addEventListener('focus', openSymbolMenu);
symbolSearch.addEventListener('click', openSymbolMenu);
symbolSearch.addEventListener('input', () => {
  symbolSearch.value = symbolSearch.value.toUpperCase().replace(/[^A-Z0-9-]/g, '');
  symbolValue.value = '';
  symbolPicker.classList.remove('invalid');
  openSymbolMenu();
  renderSymbols(symbolSearch.value);
});
symbolSearch.addEventListener('keydown', event => {
  if (event.key === 'Escape') closeSymbolMenu();
  if (event.key === 'Enter') {
    event.preventDefault();
    const first = symbolOptions.querySelector('.symbol-option');
    if (first) selectSymbol(first.dataset.symbol);
  }
});
symbolOptions.addEventListener('click', event => {
  const option = event.target.closest('.symbol-option');
  if (option) selectSymbol(option.dataset.symbol);
});
document.addEventListener('click', event => { if (!symbolPicker.contains(event.target)) closeSymbolMenu(); });

document.querySelector('#securityButton').addEventListener('click', () => {
  securityMessage.textContent = '';
  securityMessage.className = '';
  securityForm.reset();
  securityDialog.showModal();
});
document.querySelector('#logoutButton').addEventListener('click', async () => {
  await fetch('/api/auth/logout', { method: 'POST' });
  location.replace('/login');
});
document.querySelector('#closeSecurity').addEventListener('click', () => securityDialog.close());
securityDialog.addEventListener('click', event => { if (event.target === securityDialog) securityDialog.close(); });
securityForm.addEventListener('submit', async event => {
  event.preventDefault();
  const button = securityForm.querySelector('.primary');
  const values = Object.fromEntries(new FormData(securityForm));
  securityMessage.className = '';
  if (values.password !== values.confirmPassword) {
    securityMessage.textContent = 'تکرار رمز عبور یکسان نیست.';
    securityMessage.className = 'security-error';
    return;
  }
  button.disabled = true;
  try {
    const response = await fetch('/api/auth/change', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(values)
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'تغییر اطلاعات ورود ناموفق بود.');
    securityMessage.textContent = 'اطلاعات ورود تغییر کرد؛ صفحه برای ورود مجدد بارگذاری می‌شود.';
    setTimeout(() => location.reload(), 1400);
  } catch (error) {
    securityMessage.textContent = error.message;
    securityMessage.className = 'security-error';
    button.disabled = false;
  }
});

async function refreshSignals() {
  try {
    const response = await fetch('/api/signals', { cache: 'no-store' });
    const signals = await response.json();
    const active = signals.filter(s => !isFinished(s));
    document.querySelector('#count').textContent = fa.format(active.length);
    document.querySelector('#orders').innerHTML = active.length ? active.map(s => `
      <article class="order"><strong>${s.symbol} · ${s.direction}</strong><span class="status">${statusLabel(s.status)}</span>
      <small>ENTRY ${fa.format(s.entryPrice)}${s.averageFillPrice ? ' · FILL ' + fa.format(s.averageFillPrice) : ''} · TP ${fa.format(s.takeProfit)} · SL ${fa.format(s.stopLoss)} · EXPIRE ${fa.format(s.expirePrice)}${s.expireStage === 'Target' ? ' (منتقل‌شده به تارگت)' : ''} · LEVERAGE ${s.leverage ? fa.format(s.leverage) + '×' : '—'} · ${fa.format(s.positionSizeUsdt)} USDT${s.error ? ' · ERROR: ' + escapeHtml(s.error) : ''}</small>
      <button class="remove" onclick="removeSignal('${s.id}')">${s.status === 'Submitted' ? 'لغو سفارش Demo' : 'حذف از صف'}</button></article>`).join('') : '<div class="empty"><span>◇</span><strong>سیگنال فعالی وجود ندارد</strong><p>سیگنال‌های پایان‌یافته در بخش تاریخچه نتایج قرار می‌گیرند.</p></div>';
  } catch { /* status indicator already reports connectivity */ }
}

function isFinished(signal) {
  return Boolean(signal.completedAtUtc || signal.targetReachedAtUtc || signal.riskFreeClosedAtUtc || signal.stopLossReachedAtUtc || signal.removedAtUtc) ||
    ['Expired', 'Cancelled', 'Rejected', 'Deactivated', 'Error'].includes(signal.status);
}

function resultLabel(signal) {
  if (signal.outcome === 'Target') return 'تارگت خورده';
  if (signal.outcome === 'RiskFree') return 'ریسک‌فری';
  if (signal.outcome === 'StopLoss') return 'استاپ خورده';
  if (signal.outcome === 'Expired') return signal.expireReason === 'InitialBoundary' ? 'اکسپایر ـ حالت اول' : 'اکسپایر ـ حالت دوم';
  if (signal.targetReachedAtUtc) return 'تارگت خورده';
  if (signal.riskFreeClosedAtUtc) return 'ریسک‌فری';
  if (signal.stopLossReachedAtUtc) return 'استاپ خورده';
  if (signal.status === 'Expired') return 'اکسپایر شده';
  if (signal.status === 'Cancelled') return 'لغو شده';
  if (signal.status === 'Rejected' || signal.status === 'Deactivated') return 'رد شده';
  if (signal.status === 'Error') return 'ناموفق';
  return statusLabel(signal.status);
}

async function refreshHistory() {
  try {
    const response = await fetch('/api/history?days=30&limit=1000', { cache: 'no-store' });
    const items = await response.json();
    const finished = items.filter(item => isFinished({ ...item.signal, removedAtUtc: item.removedAtUtc }));
    document.querySelector('#historyCount').textContent = fa.format(finished.length);
    document.querySelector('#history').innerHTML = finished.length ? finished.map(item => {
      const s = item.signal;
      const ended = s.completedAtUtc || s.targetReachedAtUtc || s.riskFreeClosedAtUtc || s.stopLossReachedAtUtc || s.expiredAtUtc || item.removedAtUtc || item.updatedAtUtc;
      const reason = s.outcome === 'Expired' ? `<em class="expire-reason">${s.expireReason === 'InitialBoundary' ? 'عبور از مرز اولیه سقف/کف' : 'بازگشت به تارگت بعد از فعال‌شدن اکسپایر'}</em>` : '';
      return `<article class="history-item"><div><strong>${escapeHtml(s.symbol)} · ${escapeHtml(s.direction)}</strong><span class="result ${s.outcome === 'Target' || s.outcome === 'RiskFree' ? 'win' : s.outcome === 'StopLoss' ? 'loss' : ''}">${resultLabel(s)}</span></div><small>ENTRY ${fa.format(s.entryPrice)} · TP ${fa.format(s.takeProfit)} · SL ${fa.format(s.stopLoss)} · LEVERAGE ${s.leverage ? fa.format(s.leverage) + '×' : '—'} · ${fa.format(s.positionSizeUsdt)} USDT</small>${reason}<time>${new Date(ended).toLocaleString('fa-IR')}</time></article>`;
    }).join('') : '<div class="empty compact"><span>◇</span><strong>هنوز سیگنال پایان‌یافته‌ای وجود ندارد</strong></div>';
  } catch { /* history remains unchanged until the next refresh */ }
}

function statusLabel(status) {
  return ({ Pending:'در انتظار ورود', Submitting:'در حال ارسال', Submitted:'سفارش باز در Demo', Filled:'اجراشده در Demo', Expired:'اکسپایر شده', Cancelled:'لغوشده', Rejected:'ردشده', Error:'خطا' })[status] || status;
}

function escapeHtml(value) {
  const node = document.createElement('div'); node.textContent = value; return node.innerHTML;
}

async function removeSignal(id) {
  if (!confirm('این سفارش حذف یا لغو شود؟')) return;
  const response = await fetch('/api/signals/' + id, { method:'DELETE' });
  if (!response.ok) { const data = await response.json(); alert(data.error || 'عملیات ناموفق بود.'); }
  await refreshSignals(); await refreshHistory();
}

form.addEventListener('submit', async event => {
  event.preventDefault(); message.textContent = '';
  if (!symbolValue.value || !instruments.includes(symbolValue.value)) {
    symbolPicker.classList.add('invalid'); openSymbolMenu();
    message.textContent = 'لطفاً نماد را از فهرست فعال Bybit Futures انتخاب کنید.'; return;
  }
  const button = form.querySelector('.primary'); button.disabled = true;
  const values = Object.fromEntries(new FormData(form));
  localStorage.setItem('phoenixPanelKey', values.panelKey || '');
  try {
    const response = await fetch('/api/signals', { method:'POST', headers:{'Content-Type':'application/json','X-Phoenix-Key':values.panelKey || ''}, body:JSON.stringify({
      symbol: values.symbol, direction: values.direction, ceiling:Number(values.ceiling), floor:Number(values.floor), positionSizeUsdt:Number(values.positionSizeUsdt)
    })});
    const data = await response.json();
    message.textContent = response.ok ? 'سیگنال با موفقیت در صف سرور ثبت شد.' : (data.error || 'ثبت سیگنال ناموفق بود.');
    if (response.ok) await refreshSignals();
  } catch { message.textContent = 'ارتباط با سرور برقرار نشد.'; }
  finally { button.disabled = false; }
});

if ('serviceWorker' in navigator) navigator.serviceWorker.register('/sw.js');
loadInstruments(); refreshStatus(); refreshSignals(); refreshHistory();
setInterval(refreshStatus, 1000); setInterval(refreshSignals, 5000); setInterval(refreshHistory, 5000);
