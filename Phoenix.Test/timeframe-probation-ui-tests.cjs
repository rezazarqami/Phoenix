const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('./ui/node_modules/jsdom');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),flush=()=>new Promise(r=>setImmediate(r));
(async()=>{
const dom=new JSDOM(fs.readFileSync(path.join(root,'phoenix.html'),'utf8'),{url:'https://phoenix.test/phoenix',runScripts:'outside-only'}),w=dom.window,d=w.document;
let rule={timeframe:'1',maximumOpen:2,consecutiveStops:1,blocked:false},effectiveMaximumOpen=1,awaitingResult=true;
w.fetch=async url=>({ok:true,json:async()=>url==='/api/auth/me'?{isAdmin:true}:[{rule,open:1,effectiveMaximumOpen,awaitingResult}]});w.phoenixConfirm=async()=>false;
w.eval(fs.readFileSync(path.join(root,'active-signals.js'),'utf8'));await flush();
const refresh=()=>w.dispatchEvent(new w.CustomEvent('phoenix-signals-updated',{detail:[{id:'survivor',symbol:'BTCUSDT',timeframe:'1',direction:'Long',status:'Filled',filledAtUtc:'2026-11-01T10:00:00Z'}]}));
refresh();await flush();d.querySelector('[data-timeframe="1"]').click();
assert.match(d.querySelector('.timeframe-risk-settings').textContent,/انتظار نتیجهٔ معاملهٔ باز پس از یک استاپ/);assert.match(d.querySelector('.timeframe-risk-settings').textContent,/ظرفیت فعلی: 1/);assert.equal(d.querySelector('.timeframe-risk-settings input').value,'2','Configured normal capacity remains distinct from the temporary cap');
rule={...rule,consecutiveStops:0};effectiveMaximumOpen=2;awaitingResult=false;refresh();await flush();assert.match(d.querySelector('.timeframe-risk-settings').textContent,/ظرفیت فعلی: 2/);assert.doesNotMatch(d.querySelector('.timeframe-risk-settings').textContent,/انتظار نتیجهٔ معاملهٔ باز/);
rule={...rule,consecutiveStops:2,blocked:true};effectiveMaximumOpen=0;refresh();await flush();assert.match(d.querySelector('.timeframe-risk-settings').textContent,/قفل پس از دو استاپ/);assert.match(d.querySelector('.timeframe-risk-settings').textContent,/آزاد کردن/);
dom.window.close();console.log('PASS timeframe panel: single-slot probation, target reset, full lock and normal/effective capacity distinction');
})().catch(e=>{console.error(e);process.exitCode=1;});
