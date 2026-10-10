const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../Phoenix.Web/wwwroot/signal-batch-controls.js'), 'utf8');
function setup(storageBlocked = false) {
  const nodes = new Map();
  const calls = [], timers = new Map(); let nextTimer = 0;
  function node(id) {
    if (!nodes.has(id)) nodes.set(id, {value: 'All', textContent: '', disabled: false, checked: false,
      classList: {toggle() {}}, closest() {return this;},
      addEventListener(type, callback) {this[type] = callback;}});
    return nodes.get(id);
  }
  const context = {document: {querySelector: node}, localStorage: {getItem() {if (storageBlocked) throw new Error('Storage denied'); return null;}, setItem() {if (storageBlocked) throw new Error('Storage denied');}},
    faMarket: new Intl.NumberFormat('fa-IR'), AbortController,
    setTimeout(callback, ms) {timers.set(++nextTimer, {callback, ms}); return nextTimer;},
    clearTimeout(id) {timers.delete(id);},
    fetch(url, options) {
      let resolve, reject;
      const task = new Promise((yes, no) => {resolve = yes; reject = no;});
      calls.push({url, options, resolve(data) {resolve({ok: true, json: async () => data});}, reject});
      return task;
    }};
  vm.createContext(context); vm.runInContext(source.replace('watchBatch();', 'globalThis.pollBatch = pollBatch; watchBatch();'), context);
  return {nodes, node, calls, timers, context};
}
const state = running => ({running, message: running ? 'بررسی بازار' : 'متوقف شد', approved: 0, target: 1, checked: 1, rejected: 0});
const flush = () => new Promise(resolve => setImmediate(resolve));
(async () => {
  const h = setup();
  assert.equal(h.calls.length, 1);
  const initialPoll = h.calls[0];
  const start = h.node('#startBatch').click();
  assert.match(h.node('#batchStatus').textContent, /شروع/);
  assert.equal(h.node('#startBatch').disabled, true);
  await h.context.pollBatch(); // commands must suppress extra status requests
  assert.equal(h.calls.length, 2);
  await h.node('#startBatch').click(); // ignore duplicate taps
  assert.equal(h.calls.length, 2);
  h.calls[1].resolve(state(true));
  await flush();
  assert.equal(h.node('#stopBatch').disabled, false);
  initialPoll.resolve(state(false)); // a delayed pre-start response must never restore Idle
  await flush();
  assert.match(h.node('#batchStatus').textContent, /بررسی بازار/);
  h.calls.at(-1).resolve(state(true));
  await start;
  const stop = h.node('#stopBatch').click();
  assert.match(h.node('#batchStatus').textContent, /توقف/);
  const stopCall = h.calls.at(-1);
  assert.equal(stopCall.url, '/api/analysis/signal-batch/stop');
  stopCall.resolve(state(false)); await flush();
  assert.equal(h.node('#startBatch').disabled, false);
  h.calls.at(-1).resolve(state(false)); await stop;
  // A stalled POST times out visibly and reconciles status without sending it twice.
  const retry = h.node('#startBatch').click();
  const post = h.calls.at(-1);
  post.options.signal.addEventListener('abort', () => post.reject(Object.assign(new Error('timeout'), {name: 'AbortError'})));
  [...h.timers.values()].find(t => t.ms === 10000).callback();
  await flush();
  assert.match(h.node('#marketMessage').textContent, /پاسخ سرور/);
  assert.equal(h.calls.filter(c => c.options.method === 'POST').length, 3);
  h.calls.at(-1).resolve(state(true)); await retry;
  assert.equal(h.node('#stopBatch').disabled, false);
  // Polling stays single-flight even when a response is slow.
  const poll = h.context.pollBatch(); const count = h.calls.length;
  await h.context.pollBatch(); assert.equal(h.calls.length, count);
  h.calls.at(-1).resolve(state(true)); await poll;
  assert.match(h.node('#batchError').textContent, /پاسخ سرور/);
  const blocked = setup(true);
  assert.equal(blocked.node('#batchSize').value, '10');
  const blockedStart = blocked.node('#startBatch').click();
  blocked.calls[1].resolve(state(true)); await flush();
  blocked.calls[0].resolve(state(false)); await flush();
  blocked.calls.at(-1).resolve(state(true)); await blockedStart;
  assert.equal(blocked.node('#stopBatch').disabled, false);
  console.log('PASS batch controls: immediate pending feedback, duplicate prevention, stale poll rejection, stop, timeout reconciliation and single-flight polling.');
})().catch(error => {console.error(error); process.exit(1);});
