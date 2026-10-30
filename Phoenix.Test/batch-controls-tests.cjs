const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../Phoenix.Web/wwwroot/signal-batch-controls.js'), 'utf8');
function setup(storageBlocked = false) {
  const nodes = new Map();
  const calls = [], timers = new Map(), redirects = [], events = {}; let nextTimer = 0;
  function node(id) {
    if (!nodes.has(id)) nodes.set(id, {value: 'All', textContent: '', disabled: false, checked: false,
      classList: {toggle() {}}, closest() {return this;},
      addEventListener(type, callback) {this[type] = callback;}});
    return nodes.get(id);
  }
  const context = {document: {querySelector: node}, location: {replace(url) { redirects.push(url); }},
    window: {addEventListener(name, callback) { events[name] = callback; }},
    localStorage: {getItem() {if (storageBlocked) throw new Error('Storage denied'); return null;}, setItem() {if (storageBlocked) throw new Error('Storage denied');}},
    faMarket: new Intl.NumberFormat('fa-IR'), AbortController,
    setTimeout(callback, ms) {timers.set(++nextTimer, {callback, ms}); return nextTimer;},
    clearTimeout(id) {timers.delete(id);},
    fetch(url, options) {
      let resolve, reject;
      const task = new Promise((yes, no) => {resolve = yes; reject = no;});
      calls.push({url, options, resolve(data) {resolve({ok: true, json: async () => data});}, resolveResponse: resolve, reject});
      return task;
    }};
  vm.createContext(context); vm.runInContext(source.replace('watchBatch();', 'globalThis.pollBatch = pollBatch; watchBatch();'), context);
  return {nodes, node, calls, timers, context, redirects, events};
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
  [...h.timers.values()].find(t => t.ms === 15000).callback();
  await flush();
  assert.match(h.node('#marketMessage').textContent, /پاسخ سرور/);
  assert.equal(h.calls.filter(c => c.options.method === 'POST').length, 3);
  h.calls.at(-1).resolve(state(true)); await retry;
  assert.equal(h.node('#stopBatch').disabled, false);
  // Polling stays single-flight even when a response is slow.
  const poll = h.context.pollBatch(); const count = h.calls.length;
  await h.context.pollBatch(); assert.equal(h.calls.length, count);
  h.calls.at(-1).resolve(state(true)); await poll;
  assert.equal(h.node('#batchError').textContent, '', 'A verified successful command must clear its old timeout');
  // A stop can take effect even if its POST response is lost. Keep the old
  // Running snapshot off the screen and clear the warning after verification.
  const lostStop = h.node('#stopBatch').click();
  const lostPost = h.calls.at(-1);
  lostPost.options.signal.addEventListener('abort', () => lostPost.reject(Object.assign(new Error('timeout'), {name: 'AbortError'})));
  [...h.timers.values()].find(t => t.ms === 15000).callback(); await flush();
  assert.match(h.node('#batchStatus').textContent, /توقف/, 'A timed-out stop must not restore old running text');
  h.calls.at(-1).resolve(state(false)); await lostStop;
  assert.equal(h.node('#batchError').textContent, '');
  assert.equal(h.node('#stopBatch').disabled, true);
  assert.equal(h.node('#startBatch').disabled, false);
  // A stopped queue may still be releasing background work; do not offer an overlapping start.
  const drainingPoll = h.context.pollBatch();
  h.calls.at(-1).resolve({...state(false), stopping:true}); await drainingPoll;
  assert.equal(h.node('#startBatch').disabled, true);
  const idlePoll = h.context.pollBatch();
  h.calls.at(-1).resolve({...state(false), stopping:false}); await idlePoll;
  assert.equal(h.node('#startBatch').disabled, false);
  const blocked = setup(true);
  assert.equal(blocked.node('#batchSize').value, '10');
  const blockedStart = blocked.node('#startBatch').click();
  blocked.calls[1].resolve(state(true)); await flush();
  blocked.calls[0].resolve(state(false)); await flush();
  blocked.calls.at(-1).resolve(state(true)); await blockedStart;
  assert.equal(blocked.node('#stopBatch').disabled, false);
  // A stop acknowledgement can be lost while GET status is still reachable.
  // Verify it before the POST deadline, cancel the waiting POST, and unlock Start.
  const verified = setup();
  verified.calls[0].resolve({...state(true), runId:'queue-a'}); await flush();
  const earlyStop = verified.node('#stopBatch').click();
  const waitingPost = verified.calls.at(-1);
  assert.equal(waitingPost.url, '/api/analysis/signal-batch/stop?runId=queue-a');
  assert.equal(waitingPost.options.body, '{}');
  waitingPost.options.signal.addEventListener('abort', () => waitingPost.reject(Object.assign(new Error('aborted'), {name:'AbortError'})));
  [...verified.timers.values()].find(t => t.ms === 1500).callback(); await flush();
  verified.calls.at(-1).resolve({...state(false), runId:'queue-a'}); await flush();
  assert.equal(waitingPost.options.signal.aborted, true);
  assert.equal(verified.node('#startBatch').disabled, false);
  assert.equal(verified.node('#batchError').textContent, '');
  verified.calls.at(-1).resolve({...state(false), runId:'queue-a'}); await earlyStop;
  // Retry a transport failure once, always carrying the same queue identity.
  const retried = setup();
  retried.calls[0].resolve({...state(true), runId:'queue-b'}); await flush();
  const retryStop = retried.node('#stopBatch').click();
  retried.calls.at(-1).reject(Object.assign(new Error('lost response'), {name:'AbortError'})); await flush();
  const retryPosts = retried.calls.filter(c=>c.options.method === 'POST');
  assert.equal(retryPosts.length, 2);
  assert.equal(retryPosts[0].url, retryPosts[1].url);
  retryPosts[1].resolve({...state(false), runId:'queue-b'}); await flush();
  retried.calls.at(-1).resolve({...state(false), runId:'queue-b'}); await retryStop;
  // A status response for another queue is not evidence that this stop succeeded.
  const stale = setup();
  stale.calls[0].resolve({...state(true), runId:'queue-c'}); await flush();
  const staleStop = stale.node('#stopBatch').click();
  const actualPost = stale.calls.at(-1);
  [...stale.timers.values()].find(t => t.ms === 1500).callback(); await flush();
  stale.calls.at(-1).resolve({...state(false), runId:'another-queue'}); await flush();
  assert.match(stale.node('#batchStatus').textContent, /در حال ارسال دستور توقف/);
  assert.equal(stale.node('#startBatch').disabled, true);
  actualPost.resolve({...state(false), runId:'queue-c'}); await flush();
  stale.calls.at(-1).resolve({...state(false), runId:'queue-c'}); await staleStop;
  // The server sends an empty body on session expiry. Detect 401 before
  // attempting JSON, rather than trapping the user in a connection warning.
  const expired = setup(); let parsed = false;
  expired.calls[0].resolveResponse({ok:false, status:401, json:async () => {parsed = true; throw new SyntaxError();}});
  await flush();
  assert.deepEqual(expired.redirects, ['/login']);
  assert.equal(parsed, false);
  // A gateway HTML response must be identified as server unavailability and
  // recover on a fresh authoritative GET, without creating a new queue.
  const gateway = setup();
  gateway.calls[0].resolveResponse({ok:false, status:502, json:async () => {throw new SyntaxError();}});
  await flush();
  assert.match(gateway.node('#batchError').textContent, /سرور موقتاً/);
  assert.equal(gateway.node('#retryBatchConnection').hidden, false);
  assert.ok([...gateway.timers.values()].some(t=>t.ms===1500));
  const recoverGateway = gateway.node('#retryBatchConnection').click();
  gateway.calls.at(-1).resolve(state(false)); await recoverGateway;
  assert.equal(gateway.node('#batchError').textContent, '');
  assert.equal(gateway.node('#retryBatchConnection').hidden, true);
  assert.equal(gateway.node('#startBatch').disabled, false);
  assert.equal(gateway.calls.filter(c=>c.options.method==='POST').length, 0);
  // Reproduce the screenshot: Start lost its response and the next status
  // request also failed. Restore connectivity to an idle server and unlock
  // the controls, clearing the stale error without replaying the POST.
  const disconnected = setup(); disconnected.calls[0].resolve(state(false)); await flush();
  const lostStart = disconnected.node('#startBatch').click();
  disconnected.calls.at(-1).reject(Object.assign(new Error('Failed to fetch'), {name:'TypeError'})); await flush();
  disconnected.calls.at(-1).reject(Object.assign(new Error('Failed to fetch'), {name:'TypeError'})); await lostStart;
  assert.equal(disconnected.node('#startBatch').disabled, true);
  assert.match(disconnected.node('#batchStatus').textContent, /هنوز تأیید نشده/);
  const reconnect = disconnected.events.online();
  disconnected.calls.at(-1).resolve({...state(false), stopping:false}); await reconnect;
  assert.equal(disconnected.node('#startBatch').disabled, false);
  assert.equal(disconnected.node('#stopBatch').disabled, true);
  assert.equal(disconnected.node('#batchError').textContent, '');
  assert.equal(disconnected.calls.filter(c=>c.options.method==='POST').length, 1);
  // HTML with HTTP 200 is a gateway/session response, not a queue state.
  const html = setup();
  html.calls[0].resolveResponse({ok:true,status:200,headers:{get(name){return name==='Content-Type'?'text/html':'trace-html';}},json:async()=>{throw Error('must not parse');}});
  await flush();
  assert.match(html.node('#batchError').textContent,/واسط سرور/);
  assert.match(html.node('#batchError').textContent,/trace-html/);
  // Parsing failures and valid JSON of the wrong shape are distinct diagnostics.
  const truncated=setup();
  truncated.calls[0].resolveResponse({ok:true,status:200,json:async()=>{throw new SyntaxError('truncated');}});
  await flush();assert.match(truncated.node('#batchError').textContent,/ناقص یا نامعتبر/);
  const malformed=setup();malformed.calls[0].resolve({status:'ok'});
  await flush();assert.match(malformed.node('#batchError').textContent,/ساختار پاسخ/);
  // Repeated failures back off instead of flooding a poor connection every 1.5s.
  const failing=setup();failing.calls[0].reject(new TypeError('offline'));await flush();
  [...failing.timers.values()].find(t=>t.ms===1500).callback();await flush();
  failing.calls.at(-1).reject(new TypeError('offline'));await flush();
  assert.ok([...failing.timers.values()].some(t=>t.ms===3000));
  // The independent probe uses no trading command and shows a server/trace identity.
  const probe=setup();probe.calls[0].resolve(state(false));await flush();
  const diagnosis=probe.node('#diagnoseConnection').click();
  assert.equal(probe.calls.at(-1).url,'/api/analysis/connection');
  probe.calls.at(-1).resolveResponse({ok:true,status:200,headers:{get(){return 'trace-probe';}},json:async()=>({status:'ok',instance:'instance-a',startedAtUtc:'2026-10-30'})});
  await diagnosis;
  assert.match(probe.node('#connectionDiagnosis').textContent,/instance-a/);
  assert.match(probe.node('#connectionDiagnosis').textContent,/trace-probe/);
  console.log('PASS batch controls: immediate pending feedback, duplicate prevention, stale poll rejection, stop, timeout reconciliation and single-flight polling.');
})().catch(error => {console.error(error); process.exit(1);});
