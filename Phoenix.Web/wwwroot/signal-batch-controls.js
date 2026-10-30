(() => {
const faMarket = new Intl.NumberFormat('fa-IR');
const batchError = document.querySelector('#batchError');
const retryConnection = document.querySelector('#retryBatchConnection');
let batchConnectionLost = false;
let connectionFailures = 0;
let sessionEnded = false;
function showBatchError(message) {
  if (batchError) batchError.textContent = message;
  document.querySelector('#marketMessage').textContent = message;
}
function readBatchSetting(key, fallback) {
  try { return localStorage.getItem(key) || fallback; } catch { return fallback; }
}
function saveBatchSetting(key, value) {
  try { localStorage.setItem(key, value); } catch { /* Storage is optional in Android WebView. */ }
}
document.querySelector('#batchSize').value = readBatchSetting('phoenix.signal.positionSizeUsdt', '10');
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
let batchCommandPending = false;
let batchPollController = null;
let batchPollTask = null;
let batchRevision = 0;
let lastBatchState = null;
let commandUnconfirmed = false;
let unconfirmedCommand = null;
async function batchRequest(url, options, cancellation) {
  const controller = new AbortController();
  const abort = () => controller.abort();
  if (cancellation?.aborted) controller.abort();
  cancellation?.addEventListener('abort', abort, {once: true});
  const timeout = setTimeout(() => controller.abort(), options?.method === 'POST' ? 15000 : 30000);
  try {
    const response = await fetch(url, {...options, signal: controller.signal, cache: 'no-store'});
    const requestId = response.headers?.get('X-Phoenix-Request-Id');
    const suffix = requestId ? ` · کد پیگیری ${requestId}` : '';
    if (response.status === 401 || response.redirected && /\/login(?:[/?]|$)/.test(new URL(response.url, location.origin).pathname)) {
      sessionEnded = true;
      location.replace('/login'); throw new Error('نشست ورود پایان یافته است؛ دوباره وارد شوید.');
    }
    if (response.status >= 500) throw new Error(`سرور موقتاً پاسخ نمی‌دهد (HTTP ${response.status})؛ در حال بررسی دوباره ارتباط…${suffix}`);
    if (response.status === 429) throw new Error(`درخواست‌ها بیش از حد است؛ کمی بعد دوباره بررسی می‌شود.${suffix}`);
    const contentType = response.headers?.get('Content-Type');
    if (contentType && !contentType.toLowerCase().includes('json'))
      throw new Error(`پاسخ واسط سرور قابل استفاده نیست (HTTP ${response.status})؛ در حال بررسی دوباره ارتباط…${suffix}`);
    let data;
    try { data = await response.json(); }
    catch (error) {
      if (controller.signal.aborted) throw error;
      throw new Error(`پاسخ سرور ناقص یا نامعتبر است (HTTP ${response.status})؛ در حال بررسی دوباره ارتباط…${suffix}`);
    }
    if (!response.ok) {
      const error = new Error(data.error || 'دستور انجام نشد؛ وضعیت صف را بررسی کنید.');
      error.commandRejected = true;
      throw error;
    }
    if (!data || typeof data.running !== 'boolean' || typeof data.message !== 'string')
      throw new Error(`ساختار پاسخ وضعیت صف معتبر نیست؛ در حال بررسی دوباره ارتباط…${suffix}`);
    return data;
  } catch (error) {
    if (error.name === 'AbortError') throw new Error('پاسخ سرور طول کشید؛ وضعیت صف در حال بررسی است.');
    if (error.name === 'TypeError') throw new Error('پاسخ شبکه دریافت نشد؛ مسیر اینترنت یا دسترسی به سرور در حال بررسی است…');
    throw error;
  } finally { clearTimeout(timeout); cancellation?.removeEventListener('abort', abort); }
}
function stopPause(signal) {
  return new Promise((resolve, reject) => {
    const abort = () => { clearTimeout(timer); reject(new Error('Stop verification cancelled')); };
    const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve(); }, 1500);
    signal.addEventListener('abort', abort, {once: true});
    if (signal.aborted) abort();
  });
}
async function requestStop(url, options) {
  const runId = lastBatchState?.runId;
  const controller = new AbortController();
  const verification = (async () => {
    while (!controller.signal.aborted) {
      await stopPause(controller.signal);
      try {
        const state = await batchRequest('/api/analysis/signal-batch', {}, controller.signal);
        // Verify this queue only; a delayed retry must never stop a newer queue.
        if (!state.running && (!runId || state.runId === runId)) return state;
      } catch (error) { if (controller.signal.aborted) throw error; }
    }
  })();
  const stopUrl = runId ? `${url}?runId=${encodeURIComponent(runId)}` : url;
  try {
    try { return await Promise.race([batchRequest(stopUrl, options, controller.signal), verification]); }
    catch (error) {
      if (error.commandRejected || !runId) throw error;
      document.querySelector('#batchStatus').textContent = 'پاسخ توقف نرسید؛ در حال بررسی و تلاش دوباره…';
      return await Promise.race([batchRequest(stopUrl, options, controller.signal), verification]);
    }
  } finally { controller.abort(); }
}
async function batchCommand(url, options, message) {
  if (batchCommandPending) return;
  batchCommandPending = true;
  commandUnconfirmed = false;
  batchRevision++;
  batchPollController?.abort();
  document.querySelector('#startBatch').disabled = true;
  document.querySelector('#stopBatch').disabled = true;
  document.querySelector('#batchStatus').textContent = message;
  showBatchError('');
  try {
    const data = await (url.endsWith('/stop') ? requestStop(url, options) : batchRequest(url, options));
    batchCommandPending = false;
    renderBatch(data);
    return true;
  } catch (error) {
    commandUnconfirmed = !error.commandRejected;
    batchConnectionLost = commandUnconfirmed;
    if (retryConnection) retryConnection.hidden = !batchConnectionLost;
    unconfirmedCommand = commandUnconfirmed ? url : null;
    showBatchError(error.message);
    if (url.endsWith('/stop')) document.querySelector('#stopBatch').disabled = false;
  } finally {
    batchCommandPending = false;
    // Keep the pending message instead of restoring a pre-command Running state.
    // A lost response is not evidence that stop failed or that start succeeded.
    if (batchPollTask) await batchPollTask;
    const confirmed = await pollBatch();
    if (confirmed && url.endsWith('/stop') && !confirmed.running) {
      commandUnconfirmed = false;
      showBatchError('');
    }
    if (!confirmed && commandUnconfirmed) {
      document.querySelector('#batchStatus').textContent = 'وضعیت دستور هنوز تأیید نشده؛ در حال بررسی اتصال…';
      document.querySelector('#startBatch').disabled = true;
      document.querySelector('#stopBatch').disabled = false;
    }
  }
}
document.querySelector('#startBatch').addEventListener('click', async () => {
  const started = await batchCommand('/api/analysis/signal-batch', {
    method: 'POST', headers: {'Content-Type': 'application/json'},
    body: JSON.stringify({count: Number(batchCount.value), positionSizeUsdt: Number(document.querySelector('#batchSize').value),
      minimumTargetProbability: 0,
      directionFilter: document.querySelector('#batchDirection').value, chartFilter: document.querySelector('#batchChart').value,
      timeframeFilter: document.querySelector('#batchTimeframe').value, timedMode: timedMode.checked,
      durationMinutes: Number(batchDuration.value)})
  }, 'در حال ارسال دستور شروع…');
  if (started) {
    saveBatchSetting('phoenix.signal.positionSizeUsdt', document.querySelector('#batchSize').value);
  }
});
document.querySelector('#stopBatch').addEventListener('click', () =>
  batchCommand('/api/analysis/signal-batch/stop', {method: 'POST', headers: {'Content-Type': 'application/json'}, body: '{}'}, 'در حال ارسال دستور توقف…'));
function renderBatch(state) {
  if (batchCommandPending) return;
  lastBatchState = state;
  const status = document.querySelector('#batchStatus');
  status.classList.toggle('running', state.running);
  const remainingMinutes = state.endsAtUtc ? Math.max(0, Math.ceil((new Date(state.endsAtUtc) - Date.now()) / 60000)) : 0;
  const probabilityFilter = '';
  const progress = state.timedMode
    ? `تأیید ${faMarket.format(state.approved)} · پیشنهاد ${faMarket.format(state.proposed)} · باقی‌مانده حدود ${faMarket.format(remainingMinutes)} دقیقه`
    : `تأیید ${faMarket.format(state.approved)} از ${faMarket.format(state.target)}`;
  status.textContent = state.running ? `${state.message} · ${progress}${probabilityFilter} · بررسی‌شده ${faMarket.format(state.checked)} · ردشده ${faMarket.format(state.rejected)}` : state.message;
  document.querySelector('#startBatch').disabled = state.running || Boolean(state.stopping);
  document.querySelector('#stopBatch').disabled = !state.running;
}
async function pollBatch() {
  if (batchCommandPending || batchPollTask) return;
  const revision = batchRevision;
  const controller = new AbortController();
  batchPollController = controller;
  batchPollTask = (async () => {
    try {
      // Check authentication and HTTP errors before parsing JSON. A 401 has
      // no JSON body and previously appeared as an endless connection error.
      const data = await batchRequest('/api/analysis/signal-batch', {}, controller.signal);
      if (revision === batchRevision && !batchCommandPending) {
        connectionFailures = 0;
        renderBatch(data);
        if (batchConnectionLost) {
          batchConnectionLost = false;
          if (retryConnection) retryConnection.hidden = true;
          showBatchError('');
        }
        if (commandUnconfirmed && (unconfirmedCommand?.endsWith('/stop') ? !data.running : data.running)) {
          commandUnconfirmed = false; unconfirmedCommand = null; showBatchError('');
        }
        return data;
      }
    } catch (error) {
      if (revision === batchRevision && !batchCommandPending && !controller.signal.aborted) {
        connectionFailures++;
        batchConnectionLost = true;
        if (retryConnection) retryConnection.hidden = false;
        showBatchError(error.message);
      }
    }
    finally { batchPollController = null; }
  })();
  try { return await batchPollTask; }
  finally { batchPollTask = null; }
}
async function watchBatch() {
  if (sessionEnded) return;
  if (!document.hidden && (typeof navigator === 'undefined' || navigator.onLine !== false)) await pollBatch();
  const delay = batchConnectionLost ? Math.min(30000, 1500 * 2 ** Math.min(5, Math.max(0, connectionFailures - 1)))
    : lastBatchState?.stopping ? 1500 : lastBatchState?.running ? 3000 : 10000;
  setTimeout(watchBatch, delay);
}
async function reconnectBatch() {
  if (batchCommandPending) return;
  if (retryConnection) retryConnection.disabled = true;
  try {
    if (batchPollTask) await batchPollTask;
    await pollBatch();
  } finally { if (retryConnection) retryConnection.disabled = false; }
}
const diagnoseConnection = document.querySelector('#diagnoseConnection');
diagnoseConnection?.addEventListener('click', async () => {
  if (diagnoseConnection.disabled) return;
  diagnoseConnection.disabled = true;
  const output = document.querySelector('#connectionDiagnosis');
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10000);
  const started = Date.now();
  output.textContent = 'در حال آزمایش ارتباط…';
  try {
    const response = await fetch('/api/analysis/connection', {cache:'no-store', signal:controller.signal});
    if (response.status === 401) { output.textContent = 'ارتباط برقرار است؛ ورود دوباره لازم است.'; return; }
    const data = await response.json().catch(() => null);
    if (!response.ok || data?.status !== 'ok' || !data.instance)
      throw new Error(`پاسخ معتبر فونیکس نرسید (HTTP ${response.status})؛ واسط سرور یا مسیر شبکه باید بررسی شود.`);
    output.textContent = `فونیکس پاسخ داد · ${Date.now()-started} میلی‌ثانیه · سرور ${data.instance} · شروع ${data.startedAtUtc} · کد پیگیری ${response.headers.get('X-Phoenix-Request-Id')||'—'}`;
  } catch (error) {
    output.textContent = error.name === 'AbortError' ? 'تا ۱۰ ثانیه پاسخی نرسید؛ این آزمایش به‌تنهایی سرور را از مشکل مسیر اینترنت تفکیک نمی‌کند.'
      : error.name === 'TypeError' ? 'ارتباط شبکه تا فونیکس برقرار نشد؛ همین آزمایش را با اینترنت دیگر مقایسه کنید.' : error.message;
  } finally { clearTimeout(timeout); diagnoseConnection.disabled = false; }
});
retryConnection?.addEventListener('click', reconnectBatch);
if (typeof window !== 'undefined') window.addEventListener('online', reconnectBatch);
document.addEventListener?.('visibilitychange', () => {
  if (document.visibilityState === 'visible' && batchConnectionLost) reconnectBatch();
});
watchBatch();

})();
