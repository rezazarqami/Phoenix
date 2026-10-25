const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const root = path.join(__dirname, '../Phoenix.Web/wwwroot');
function element(tag) {
  return {tag, children: [], hidden: false, disabled: false, textContent: '', value: 'All', style: {}, listeners: {},
    append(...items) { this.children.push(...items); },
    addEventListener(type, fn) { this.listeners[type] = fn; },
    showModal() { this.open = true; }, close() { this.open = false; this.listeners.close?.(); },
    remove() { this.removed = true; }, focus() { this.focused = true; }};
}
async function confirmations() {
  const body = element('body');
  const context = {window: {}, document: {body, createElement: element}};
  vm.runInNewContext(fs.readFileSync(path.join(root, 'confirmation.js'), 'utf8'), context);
  for (const choice of ['accept', 'cancel', 'escape']) {
    const result = context.window.phoenixConfirm('لغو سیگنال؟');
    const dialog = body.children.at(-1);
    assert.equal(dialog.open, true);
    const [message, accept, cancel] = dialog.children[0].children;
    assert.equal(message.textContent, 'لغو سیگنال؟');
    assert.equal(cancel.focused, true);
    if (choice === 'escape') dialog.listeners.cancel({preventDefault() {}});
    else (choice === 'accept' ? accept : cancel).onclick();
    assert.equal(await result, choice === 'accept');
    assert.equal(dialog.removed, true);
  }
}
async function bulk({confirmed = true, statusCode = 200, networkError = false, refreshError = false, direction = 'All'} = {}) {
  const nodes = Object.fromEntries(['bulkControls','bulkStatus','resumeEntriesButton','cancelPendingButton','closePositionsButton','cancelDirection'].map(x => ['#'+x, element('button')]));
  nodes['#cancelDirection'].value = direction;
  let posts = [], refreshes = 0;
  const context = {
    document: {querySelector: x => nodes[x], querySelectorAll: () => [nodes['#cancelPendingButton'], nodes['#closePositionsButton']]},
    phoenixConfirm: async () => confirmed,
    confirm: () => { throw new Error('Native confirmation must not be called in Android'); },
    fetch: async (url, options) => {
      if (url === '/api/auth/me') return {json: async () => ({isAdmin: true})};
      if (url === '/api/positions/entry-pause') return {json: async () => ({paused: false})};
      posts.push(url); assert.equal(options.method, 'POST');
      if (networkError) throw new Error('Network disconnected');
      return {ok: statusCode === 200, status: statusCode, json: async () => {
        if (statusCode !== 200) throw new Error('Empty response');
        return {cancelled: 3};
      }};
    },
    refreshSignals: async () => { refreshes++; if (refreshError) throw new Error('Refresh failed'); },
    refreshHistory: async () => { refreshes++; }
  };
  await vm.runInNewContext(fs.readFileSync(path.join(root, 'bulk-controls.js'), 'utf8'), context);
  await nodes['#cancelPendingButton'].onclick();
  assert.equal(nodes['#cancelPendingButton'].disabled, false);
  if (!confirmed) { assert.equal(posts.length, 0); return; }
  assert.deepEqual(posts, ['/api/signals/cancel-pending/' + direction]);
  const text = nodes['#bulkStatus'].textContent;
  if (networkError) assert.match(text, /Network disconnected/);
  else if (statusCode === 401) assert.match(text, /منقضی/);
  else if (statusCode === 403) assert.match(text, /مجوز/);
  else { assert.match(text, /3 سیگنال/); assert.ok(refreshes >= 1); }
  if (refreshError) assert.match(text, /به‌روزرسانی نمایش ناموفق/);
}
async function single(confirmed, statusCode) {
  const source = fs.readFileSync(path.join(root, 'app.js'), 'utf8');
  const fn = source.slice(source.indexOf('async function removeSignal(id)'), source.indexOf("\nform.addEventListener", source.indexOf('async function removeSignal(id)')));
  const message = {textContent: ''}; let calls = 0;
  const context = {message, phoenixConfirm: async () => confirmed,
    confirm: () => { throw new Error('Native confirm'); },
    fetch: async (url, options) => { calls++; assert.equal(url, '/api/signals/test-id'); assert.equal(options.method, 'DELETE'); return {ok: statusCode === 200, status: statusCode, json: async () => statusCode === 200 ? {message: 'سیگنال لغو شد'} : {error: 'لغو انجام نشد'} }; },
    refreshSignals: async () => {}, refreshHistory: async () => {}};
  vm.createContext(context); vm.runInContext(fn, context);
  await context.removeSignal('test-id');
  assert.equal(calls, confirmed ? 1 : 0);
  if (confirmed) assert.match(message.textContent, statusCode === 200 ? /لغو شد/ : /لغو انجام نشد/);
}
(async () => {
  await confirmations();
  for (const direction of ['All','Long','Short']) await bulk({direction});
  await bulk({confirmed:false}); await bulk({statusCode:401}); await bulk({statusCode:403});
  await bulk({networkError:true}); await bulk({refreshError:true});
  await single(true, 200); await single(true, 409); await single(false, 200);
  console.log('PASS cancellation UI: HTML accept/cancel/Escape, bulk All/Long/Short, no-op cancellation, expired session, permission error, network error, refresh failure and individual cancellation.');
})().catch(error => { console.error(error); process.exit(1); });
