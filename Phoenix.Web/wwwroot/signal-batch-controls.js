(() => {
const faMarket = new Intl.NumberFormat('fa-IR');
const batchError = document.querySelector('#batchError');
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
document.querySelector('#batchMinTarget').value = readBatchSetting('phoenix.signal.minimumTargetProbability', '55');
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
async function batchRequest(url, options) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10000);
  try {
    const response = await fetch(url, {...options, signal: controller.signal, cache: 'no-store'});
    if (response.status === 401) { location.replace('/login'); throw new Error('نشست ورود پایان یافته است؛ دوباره وارد شوید.'); }
    const data = await response.json().catch(() => { throw new Error('پاسخ سرور معتبر نیست؛ دوباره تلاش کنید.'); });
    if (!response.ok) {
      const error = new Error(data.error || 'دستور انجام نشد؛ وضعیت صف را بررسی کنید.');
      error.commandRejected = true;
      throw error;
    }
    return data;
  } catch (error) {
    if (error.name === 'AbortError') throw new Error('پاسخ سرور طول کشید؛ وضعیت صف در حال بررسی است.');
    throw error;
  } finally { clearTimeout(timeout); }
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
    const data = await batchRequest(url, options);
    batchCommandPending = false;
    renderBatch(data);
    return true;
  } catch (error) {
    commandUnconfirmed = !error.commandRejected;
    unconfirmedCommand = commandUnconfirmed ? url : null;
    showBatchError(error.message);
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
      minimumTargetProbability: Number(document.querySelector('#batchMinTarget').value),
      directionFilter: document.querySelector('#batchDirection').value, chartFilter: document.querySelector('#batchChart').value,
      timeframeFilter: document.querySelector('#batchTimeframe').value, timedMode: timedMode.checked,
      durationMinutes: Number(batchDuration.value)})
  }, 'در حال ارسال دستور شروع…');
  if (started) {
    saveBatchSetting('phoenix.signal.positionSizeUsdt', document.querySelector('#batchSize').value);
    saveBatchSetting('phoenix.signal.minimumTargetProbability', document.querySelector('#batchMinTarget').value);
  }
});
document.querySelector('#stopBatch').addEventListener('click', () =>
  batchCommand('/api/analysis/signal-batch/stop', {method: 'POST'}, 'در حال ارسال دستور توقف…'));
function renderBatch(state) {
  if (batchCommandPending) return;
  lastBatchState = state;
  const status = document.querySelector('#batchStatus');
  status.classList.toggle('running', state.running);
  const remainingMinutes = state.endsAtUtc ? Math.max(0, Math.ceil((new Date(state.endsAtUtc) - Date.now()) / 60000)) : 0;
  const probabilityFilter = state.minimumTargetProbability > 0 ? ` · حداقل شباهت به تارگت‌ها ${faMarket.format(state.minimumTargetProbability)}٪` : '';
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
  const timeout = setTimeout(() => controller.abort(), 8000);
  batchPollTask = (async () => {
    try {
      const response = await fetch('/api/analysis/signal-batch', {cache: 'no-store', signal: controller.signal});
      const data = await response.json();
      if (response.status === 401) return location.replace('/login');
      if (response.ok && revision === batchRevision && !batchCommandPending) {
        renderBatch(data);
        if (commandUnconfirmed && (unconfirmedCommand?.endsWith('/stop') ? !data.running : data.running)) {
          commandUnconfirmed = false; unconfirmedCommand = null; showBatchError('');
        }
        return data;
      }
    } catch (error) {
      if (revision === batchRevision && !batchCommandPending && error.name !== 'AbortError')
        showBatchError('دریافت وضعیت صف ناموفق بود؛ اتصال را بررسی کنید.');
    }
    finally { clearTimeout(timeout); batchPollController = null; }
  })();
  try { return await batchPollTask; }
  finally { batchPollTask = null; }
}
async function watchBatch() {
  await pollBatch();
  setTimeout(watchBatch, lastBatchState?.stopping ? 1500 : lastBatchState?.running ? 3000 : 10000);
}
watchBatch();

})();
