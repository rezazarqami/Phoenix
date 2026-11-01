const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('./ui/node_modules/jsdom');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),flush=()=>new Promise(r=>setImmediate(r));
(async()=>{
const dom=new JSDOM(fs.readFileSync(path.join(root,'notifications.html'),'utf8'),{url:'https://phoenix.test/notifications',runScripts:'outside-only'}),w=dom.window,d=w.document;w.setInterval=()=>0;
const locked='تایم‌فریم ۱ دقیقه به‌دلیل دو استاپ پیاپی در BTCUSDT و ETHUSDT قفل شد.',blocked='SOLUSDT به نقطهٔ ورود رسید، اما به‌دلیل قفل بودن تایم‌فریم ۱ دقیقه معامله باز نشد.';
w.fetch=async url=>({ok:true,json:async()=>url.includes('/auth/me')?{username:'owner',isAdmin:true}:url.includes('wallet-recovery')?{state:'waiting'}:{events:[{id:'timeframe:lock:TimeframeLocked',kind:'TimeframeLocked',symbol:'تایم‌فریم ۱ دقیقه',sequence:1,at:'2026-11-01T10:00:00Z',reason:locked},{id:'00000000-0000-4000-8000-000000000001:EntryBlocked',kind:'EntryBlocked',symbol:'SOLUSDT',sequence:2,at:'2026-11-01T10:01:00Z',reason:blocked}],hasMore:false,focusFound:true}});
w.eval(fs.readFileSync(path.join(root,'notifications.js'),'utf8'));await flush();await flush();
assert.equal(d.querySelectorAll('.event-card').length,2);assert.match(d.querySelector('[data-kind=TimeframeLocked]').textContent,/BTCUSDT.*ETHUSDT/);assert.equal(d.querySelector('[data-kind=TimeframeLocked] details'),null,'Account lock does not offer a misleading trading chart');assert.ok(d.querySelector('[data-kind=EntryBlocked]').textContent.includes(blocked));
d.querySelector('#journal-tab-results').click();await flush();assert.equal(d.querySelectorAll('.event-card').length,1);assert.ok(d.querySelector('[data-kind=EntryBlocked]'));assert.equal(d.querySelectorAll('.event-wallet').length,0,'Unopened trade has no fabricated wallet result');
dom.window.close();console.log('PASS timeframe alert journal: full reasons, both stop symbols, blocked result, no misleading chart or wallet');
})().catch(e=>{console.error(e);process.exitCode=1;});
