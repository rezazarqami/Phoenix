(() => {
  const panel = document.querySelector('#panel-statistics');
  const strategy = document.querySelector('#statisticsStrategy');
  const rows = document.querySelector('#statisticsRows');
  const status = document.querySelector('#statisticsStatus');
  const number = new Intl.NumberFormat('fa-IR');
  let controller;
  let timer;
  let generation = 0;

  async function refresh() {
    if (panel.hidden || document.hidden) return;
    controller?.abort();
    const request = new AbortController();
    controller = request;
    const timeout = setTimeout(() => request.abort(), 12000);
    try {
      const response = await fetch('/api/crypto-statistics?strategy=' + encodeURIComponent(strategy.value),
        { cache: 'no-store', signal: request.signal });
      if (!response.ok) throw new Error('request failed');
      const data = await response.json();
      if (controller !== request) return;
      const items = data.items;
      const totals = { total: 0, targets: 0, stops: 0 };
      const fragment = document.createDocumentFragment();
      items.forEach((item, index) => {
        for (const key of Object.keys(totals)) totals[key] += item[key];
        const row = document.createElement('tr');
        [index + 1, item.symbol, item.total, item.open, item.targets, item.stops, item.riskFree, item.otherClosed]
          .forEach((value, column) => {
            const cell = document.createElement(column === 1 ? 'th' : 'td');
            if (column === 1) { cell.scope = 'row'; cell.dir = 'ltr'; }
            cell.textContent = column === 1 ? value : number.format(value);
            if (column === 4) cell.className = 'stat-target';
            if (column === 5) cell.className = 'stat-stop';
            row.append(cell);
          });
        fragment.append(row);
      });
      rows.replaceChildren(fragment);
      if (!items.length) showRow('هنوز معامله‌ای برای این استراتژی ثبت نشده است.');
      document.querySelector('#statisticsCoins').textContent = number.format(items.length);
      for (const key of Object.keys(totals))
        document.querySelector('#statistics' + key[0].toUpperCase() + key.slice(1)).textContent = number.format(totals[key]);
      status.classList.remove('statistics-error');
      status.textContent = 'آخرین به‌روزرسانی: ' + new Date(data.updatedAtUtc).toLocaleTimeString('fa-IR');
    } catch {
      if (controller !== request) return;
      status.classList.add('statistics-error');
      status.textContent = 'دریافت آمار ناموفق بود؛ اطلاعات نمایش‌داده‌شده ممکن است قدیمی باشد. تلاش مجدد خودکار انجام می‌شود.';
    } finally { clearTimeout(timeout); }
  }

  function showRow(text) {
    const row = document.createElement('tr');
    const cell = document.createElement('td');
    cell.colSpan = 8;
    cell.textContent = text;
    row.append(cell);
    rows.replaceChildren(row);
  }
  function schedule() {
    const current = ++generation;
    clearTimeout(timer);
    if (panel.hidden || document.hidden) { controller?.abort(); controller = null; return; }
    refresh().finally(() => {
      if (current === generation) timer = setTimeout(schedule, 5000);
    });
  }
  strategy.addEventListener('change', () => {
    controller?.abort(); controller = null;
    showRow('در حال دریافت آمار…');
    for (const id of ['Coins', 'Total', 'Targets', 'Stops'])
      document.querySelector('#statistics' + id).textContent = '—';
    schedule();
  });
  window.addEventListener('phoenix-tab-changed', schedule);
  document.addEventListener('visibilitychange', schedule);
})();
