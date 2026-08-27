const fa = new Intl.NumberFormat('fa-IR', { maximumFractionDigits: 6 });
const $ = selector => document.querySelector(selector);
const ui = {
  symbol: $('#signalSymbol'), symbolMenu: $('#signalSymbolMenu'), symbolToggle: $('#signalSymbolToggle'),
  interval: $('#signalInterval'), type: $('#signalChartType'), size: $('#positionSize'), quantity: $('#planQuantity'),
  range: $('#analyzeVisible'), chart: $('#signalChart'), ceiling: $('#candidateCeiling'), floor: $('#candidateFloor'),
  direction: $('#candidateDirection'), confidence: $('#candidateConfidence'), rationale: $('#candidateRationale'),
  approve: $('#approveSignal'), message: $('#signalMessage'), dialog: $('#confirmDialog')
};

let instruments = [], chart, series, candidate, plan, lightChart = false;
let priceLines = [], loadedKey = '', requestNumber = 0, candidateController, previewController;
let candidateLoading = false, previewTimer;

async function loadInstruments() {
  try {
    const response = await fetch('/api/analysis/instruments', { cache: 'no-store' });
    if (response.status === 401) return location.replace('/analysis/login');
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'فهرست نمادها دریافت نشد.');
    instruments = (data.symbols || []).sort();
  } catch (error) {
    instruments = [];
    ui.message.textContent = error.message;
  }
}

function renderSymbolMenu() {
  const query = ui.symbol.value.trim().toUpperCase();
  const matches = instruments.filter(symbol => symbol.includes(query)).slice(0, 150);
  ui.symbolMenu.innerHTML = matches.map(symbol =>
    `<button type="button" data-symbol="${symbol}"><b>${symbol}</b><small>Bybit Linear</small></button>`
  ).join('') || '<div class="signal-symbol-empty">نمادی پیدا نشد.</div>';
  ui.symbolMenu.hidden = false;
}

ui.symbol.addEventListener('input', () => {
  ui.symbol.value = ui.symbol.value.toUpperCase().replace(/[^A-Z0-9-]/g, '');
  renderSymbolMenu();
});
ui.symbol.addEventListener('focus', renderSymbolMenu);
ui.symbolToggle.addEventListener('click', event => { event.stopPropagation(); renderSymbolMenu(); });
ui.symbolMenu.addEventListener('click', event => {
  const button = event.target.closest('button[data-symbol]');
  if (!button) return;
  ui.symbol.value = button.dataset.symbol;
  ui.symbolMenu.hidden = true;
  findCandidate(false);
});
ui.symbol.addEventListener('keydown', event => {
  if (event.key !== 'Enter') return;
  event.preventDefault();
  ui.symbolMenu.hidden = true;
  findCandidate(false);
});
document.addEventListener('click', event => {
  if (!event.target.closest('.signal-symbol-control')) ui.symbolMenu.hidden = true;
});

function themeOptions() {
  return lightChart ? {
    layout: { background: { color: '#fff' }, textColor: '#4b5652' },
    grid: { vertLines: { color: 'transparent' }, horzLines: { color: 'transparent' } },
    rightPriceScale: { borderColor: '#cfd7d4' }, timeScale: { borderColor: '#cfd7d4' }
  } : {
    layout: { background: { color: '#0d0b08' }, textColor: '#9d9078' },
    grid: { vertLines: { color: '#211a10' }, horzLines: { color: '#211a10' } },
    rightPriceScale: { borderColor: '#493a23' }, timeScale: { borderColor: '#493a23' }
  };
}

function makeChart(candles) {
  if (chart) chart.remove();
  ui.chart.innerHTML = '';
  chart = LightweightCharts.createChart(ui.chart, {
    width: ui.chart.clientWidth, height: ui.chart.clientHeight,
    layout: { background: { color: lightChart ? '#fff' : '#0d0b08' }, textColor: lightChart ? '#554b3d' : '#9d9078', fontFamily: 'Arial' },
    grid: { vertLines: { color: lightChart ? 'transparent' : '#211a10' }, horzLines: { color: lightChart ? 'transparent' : '#211a10' } },
    rightPriceScale: { borderColor: lightChart ? '#d8c9ab' : '#493a23', mode: LightweightCharts.PriceScaleMode.Logarithmic },
    timeScale: { borderColor: lightChart ? '#d8c9ab' : '#493a23', timeVisible: true }
  });
  if (ui.type.value === 'line') {
    series = chart.addLineSeries({ color: '#168b6d', lineWidth: 2 });
    series.setData(candles.map(c => ({ time: c.openTime / 1000, value: Number(c.close) })));
  } else {
    series = chart.addCandlestickSeries({ upColor: '#d7aa45', downColor: '#a7663d', borderVisible: false, wickUpColor: '#f1cf76', wickDownColor: '#ce8050' });
    series.setData(candles.map(c => ({ time: c.openTime / 1000, open: Number(c.open), high: Number(c.high), low: Number(c.low), close: Number(c.close) })));
  }
  const start = Math.max(0, candles.length - 220);
  chart.timeScale().setVisibleLogicalRange({ from: start, to: candles.length - 1 });
  chart.timeScale().subscribeVisibleLogicalRangeChange(() => {
    if (candidateLoading) return;
    ui.range.disabled = false;
    ui.message.style.color = '';
    ui.message.textContent = 'محدوده نمودار تغییر کرد؛ برای تحلیل سقف و کف همین بخش، «تحلیل محدوده قابل مشاهده» را بزنید.';
  });
  priceLines = [];
  window.signalDrawingTools?.attach(chart, series);
}

new ResizeObserver(() => {
  if (chart) chart.applyOptions({ width: ui.chart.clientWidth, height: ui.chart.clientHeight });
}).observe(ui.chart);

function line(price, color, title, style = 2) {
  return series.createPriceLine({ price: Number(price), color, lineWidth: 1, lineStyle: style, axisLabelVisible: true, title });
}
function clearPriceLines() {
  if (series) priceLines.forEach(item => series.removePriceLine(item));
  priceLines = [];
}
function drawLevels() {
  if (!series || !plan) return;
  clearPriceLines();
  priceLines = [
    line(ui.ceiling.value, '#f0b90b', 'MAJOR HIGH'), line(ui.floor.value, '#a96cf2', 'MAJOR LOW'),
    line(plan.entryPrice, '#d39b35', 'ENTRY', 0), line(plan.takeProfit, '#f1cf76', 'TP', 0),
    line(plan.stopLoss, '#ff6175', 'SL', 0)
  ];
}
function setText(selector, value, suffix = '') { $(selector).textContent = value == null ? '—' : fa.format(value) + suffix; }
function requestBody() {
  return {
    symbol: ui.symbol.value.trim().toUpperCase(),
    direction: document.querySelector('.direction-switch button.active')?.dataset.direction || candidate?.direction || 'Long',
    ceiling: Number(ui.ceiling.value), floor: Number(ui.floor.value), positionSizeUsdt: Number(ui.quantity.value)
  };
}

async function refreshPreview() {
  if (candidateLoading || !candidate || Number(ui.ceiling.value) <= Number(ui.floor.value)) return;
  previewController?.abort();
  ui.approve.disabled = true;
  const panel = $('.candidate-panel');
  if (candidate.isBurned) {
    panel.classList.add('no-active');
    ui.direction.classList.add('burned');
    ui.direction.textContent = 'سیگنال فعالی نیست';
    const touched = new Date(candidate.entryTouchedTime).toLocaleString('fa-IR');
    ui.rationale.textContent = `آخرین ساختار در ${touched} به نقطه ورود رسیده و سوخته است؛ از فهرست پیشنهادهای قابل معامله حذف شد.`;
    ui.message.textContent = 'برای این محدوده سیگنال فعال و قابل ثبت وجود ندارد.';
    plan = null;
    clearPriceLines();
    return;
  }
  panel.classList.remove('no-active');
  ui.direction.classList.remove('burned');
  if (!Number(ui.quantity.value)) {
    ui.message.textContent = 'مقدار ورودی دلاری را وارد کنید.';
    return;
  }
  const controller = new AbortController();
  previewController = controller;
  const currentCandidate = candidate;
  try {
    const response = await fetch('/api/analysis/signal-preview', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(requestBody()), signal: controller.signal
    });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error);
    if (controller !== previewController || candidate !== currentCandidate) return;
    plan = data;
    setText('#planEntry', data.entryPrice); setText('#planTarget', data.takeProfit); setText('#planStop', data.stopLoss);
    setText('#planLeverage', data.leverage, '×'); setText('#planRiskFree', data.riskFreePrice);
    drawLevels();
    ui.direction.textContent = candidate.direction === 'Long' ? 'LONG پیشنهادی' : 'SHORT پیشنهادی';
    ui.message.textContent = '';
    ui.approve.disabled = false;
  } catch (error) {
    if (error.name !== 'AbortError' && controller === previewController) ui.message.textContent = error.message;
  }
}

function selectedKey() { return `${ui.symbol.value.trim().toUpperCase()}|${ui.interval.value}|${ui.type.value}`; }
function setCandidateLoading(value) {
  candidateLoading = value;
  $('.signal-chart-card').classList.toggle('loading-chart', value);
  ui.range.disabled = value || !chart;
  if (value) ui.approve.disabled = true;
}

async function findCandidate(useVisible = false) {
  const symbol = ui.symbol.value.trim().toUpperCase();
  const key = selectedKey();
  if (!symbol) return;
  if (instruments.length && !instruments.includes(symbol)) {
    ui.message.textContent = 'لطفاً یک نماد را از فهرست فعال Bybit انتخاب کنید.';
    renderSymbolMenu();
    return;
  }
  if (useVisible && (!chart || loadedKey !== key)) useVisible = false;

  candidateController?.abort();
  previewController?.abort();
  const controller = new AbortController();
  candidateController = controller;
  const currentRequest = ++requestNumber;
  let timedOut = false;
  const timeout = setTimeout(() => { timedOut = true; controller.abort(); }, 30000);
  setCandidateLoading(true);
  ui.message.style.color = '';
  ui.message.textContent = `در حال دریافت ${symbol} · ${ui.interval.options[ui.interval.selectedIndex].text}…`;
  $('#candidateMeta').textContent = 'در حال بارگذاری انتخاب جدید…';

  try {
    const query = new URLSearchParams({
      symbol, interval: ui.interval.value, chartType: ui.type.value, depth: '5', positionSizeUsdt: ui.size.value
    });
    if (useVisible) {
      const visible = chart.timeScale().getVisibleRange();
      if (!visible) throw new Error('محدوده قابل مشاهده نمودار مشخص نیست.');
      query.set('from', Math.floor(Number(visible.from)));
      query.set('to', Math.ceil(Number(visible.to)));
    }
    const response = await fetch(`/api/analysis/signal-candidate?${query}`, { cache: 'no-store', signal: controller.signal });
    if (response.status === 401) return location.replace('/analysis/login');
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'تحلیل ناموفق بود.');
    if (controller !== candidateController || currentRequest !== requestNumber || key !== selectedKey()) return;

    candidate = data.candidate;
    ui.symbol.value = candidate.symbol;
    ui.ceiling.value = candidate.ceiling; ui.floor.value = candidate.floor;
    ui.ceiling.disabled = false; ui.floor.disabled = false;
    ui.rationale.textContent = candidate.rationale;
    ui.confidence.textContent = `${fa.format(candidate.rangeCandleCount)} کندل · امتیاز ${fa.format(candidate.confidence)}`;
    ui.direction.textContent = candidate.direction === 'Long' ? 'LONG پیشنهادی' : 'SHORT پیشنهادی';
    $('#candidateSymbol').textContent = candidate.symbol;
    const start = new Date(candidate.rangeStartTime).toLocaleDateString('fa-IR');
    const end = new Date(candidate.rangeEndTime).toLocaleDateString('fa-IR');
    $('#candidateMeta').textContent = `${ui.interval.options[ui.interval.selectedIndex].text} · ${ui.type.value === 'line' ? 'خطی بر اساس Close' : 'کندل‌استیک'} · محدوده ${start} تا ${end}`;
    setText('#candidateLastPrice', candidate.lastPrice);
    document.querySelectorAll('.direction-switch button').forEach(button => button.classList.toggle('active', button.dataset.direction === candidate.direction));
    if (!useVisible) { makeChart(data.candles); loadedKey = key; }
    setCandidateLoading(false);
    await refreshPreview();
  } catch (error) {
    if (controller !== candidateController) return;
    if (error.name === 'AbortError' && !timedOut) return;
    ui.message.textContent = timedOut ? 'دریافت نمودار بیش از حد طول کشید؛ دوباره نماد یا تایم‌فریم را انتخاب کنید.' : error.message;
    $('#candidateMeta').textContent = 'بارگذاری ناموفق بود';
  } finally {
    clearTimeout(timeout);
    if (controller === candidateController) setCandidateLoading(false);
  }
}

function schedulePreview() { clearTimeout(previewTimer); previewTimer = setTimeout(refreshPreview, 350); }
[ui.ceiling, ui.floor].forEach(input => input.addEventListener('input', schedulePreview));
ui.quantity.addEventListener('input', () => {
  ui.size.value = ui.quantity.value;
  if (Number(ui.quantity.value) > 0) localStorage.setItem('phoenix.signal.positionSizeUsdt', ui.quantity.value);
  schedulePreview();
});
ui.size.addEventListener('input', () => {
  ui.quantity.value = ui.size.value;
  if (Number(ui.size.value) > 0) localStorage.setItem('phoenix.signal.positionSizeUsdt', ui.size.value);
  schedulePreview();
});
document.querySelectorAll('.direction-switch button').forEach(button => button.addEventListener('click', () => {
  document.querySelectorAll('.direction-switch button').forEach(item => item.classList.remove('active'));
  button.classList.add('active');
  ui.direction.textContent = button.dataset.direction === 'Long' ? 'LONG انتخابی' : 'SHORT انتخابی';
  refreshPreview();
}));
ui.range.addEventListener('click', () => findCandidate(true));
ui.interval.addEventListener('change', () => { loadedKey = ''; findCandidate(false); });
ui.type.addEventListener('change', () => { loadedKey = ''; findCandidate(false); });
$('#toggleChartTheme').addEventListener('click', event => {
  lightChart = !lightChart;
  event.currentTarget.textContent = lightChart ? '☾ پس‌زمینه تیره' : '☀ پس‌زمینه سفید';
  if (chart) chart.applyOptions(themeOptions());
});

ui.approve.addEventListener('click', () => {
  const body = requestBody();
  $('#confirmSummary').innerHTML = `${body.symbol} · ${body.direction}<br>HIGH ${body.ceiling} · LOW ${body.floor}<br>ENTRY ${plan.entryPrice} · TP ${plan.takeProfit} · SL ${plan.stopLoss}<br>ورودی ${body.positionSizeUsdt} USDT · LEVERAGE ${plan.leverage}×`;
  ui.dialog.showModal();
});
$('#cancelConfirm').addEventListener('click', () => ui.dialog.close());
$('#finalConfirm').addEventListener('click', async () => {
  const button = $('#finalConfirm'); button.disabled = true;
  try {
    const response = await fetch('/api/analysis/signals/confirm', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ confirmed: true, signal: requestBody() }) });
    const data = await response.json();
    if (!response.ok) throw new Error(data.error || 'ثبت ناموفق بود.');
    ui.dialog.close(); ui.approve.disabled = true; ui.message.style.color = '#e8b84b';
    ui.message.textContent = `سیگنال ${data.symbol} با موفقیت وارد صف Phoenix شد.`;
  } catch (error) { ui.dialog.close(); ui.message.textContent = error.message; }
  finally { button.disabled = false; }
});
$('#analysisLogout').addEventListener('click', async () => {
  await fetch('/api/analysis/auth/logout', { method: 'POST' });
  location.replace('/analysis/login');
});

localStorage.removeItem('phoenix.signal.quantity');
const savedPositionSize = localStorage.getItem('phoenix.signal.positionSizeUsdt') || ui.size.value;
ui.size.value = savedPositionSize; ui.quantity.value = savedPositionSize;
const requestedSymbol = new URLSearchParams(location.search).get('symbol');
if (requestedSymbol) ui.symbol.value = requestedSymbol.toUpperCase().replace(/[^A-Z0-9-]/g, '');
loadInstruments().then(() => findCandidate(false));
