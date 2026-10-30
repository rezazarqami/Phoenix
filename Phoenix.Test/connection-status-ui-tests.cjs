const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{JSDOM}=require('./ui/node_modules/jsdom');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot');
const flush=()=>new Promise(r=>setImmediate(r));
(async()=>{
 const dom=new JSDOM(fs.readFileSync(path.join(root,'phoenix.html'),'utf8'),{url:'https://phoenix.test/phoenix',runScripts:'outside-only'}),w=dom.window;
 let now=1000000;w.Date.now=()=>now;Object.defineProperty(w.document,'hidden',{value:false});
 const intervals=[],timers=new Map();let serial=0;const pending=[];
 w.setInterval=(fn,ms)=>{intervals.push({fn,ms});return ++serial;};w.setTimeout=(fn,ms)=>{timers.set(++serial,{fn,ms});return serial;};w.clearTimeout=id=>timers.delete(id);
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;};
 w.fetch=async(url,options={})=>{
  if(url==='/api/status')return new Promise((resolve,reject)=>{pending.push({resolve,reject});options.signal.addEventListener('abort',()=>reject(new w.DOMException('timeout','AbortError')));});
  return {ok:true,json:async()=>url==='/api/auth/me'?{isAdmin:true}:url==='/api/instruments'?{symbols:[]}:[]};
 };
 w.eval(fs.readFileSync(path.join(root,'app.js'),'utf8'));await flush();
 const badge=w.document.getElementById('connection');const poll=intervals.find(t=>t.ms===1000).fn;
 const check={status:'connected',checkedAtUtc:'2026-10-30T12:00:00Z',lastSuccessAtUtc:'2026-10-30T12:00:00Z',ageSeconds:0};
 const data=(status='connected',changes={})=>({tradingEnvironment:'Real',tradingEnabled:false,connection:{status,reason:status==='degraded'?'اختلال جزئی در یک نماد':status==='error'?'دسترسی حساب ناموفق است':'بررسی واقعی اتصال',publicApi:check,account:check,monitoring:check,affectedSymbolCount:0,...changes}});
 async function answer(body,ok=true,status=200){pending.shift().resolve({ok,status,json:async()=>body});await flush();}
 await answer(data('unknown',{account:{status:'checking'}}));assert.ok(badge.classList.contains('warn'));assert.ok(!badge.classList.contains('ok'));
 poll();await answer(data());assert.ok(badge.classList.contains('ok'));
 poll();await answer(data('degraded',{affectedSymbolCount:1,affectedSymbols:['FAILUSDT']}));
 assert.ok(badge.classList.contains('warn'));assert.ok(!badge.classList.contains('bad'),'One symbol failure must not be labeled full exchange disconnection');
 badge.click();assert.match(w.document.getElementById('connectionDetailsBody').textContent,/FAILUSDT/);assert.match(w.document.getElementById('connectionDetailsBody').textContent,/خاموش در تنظیمات/);
 poll();await answer(data('error',{account:{...check,status:'error',error:'Invalid key'}}));assert.ok(badge.classList.contains('bad'));assert.match(w.document.getElementById('connectionDetailsBody').textContent,/Invalid key/);
 poll();await answer(data());assert.ok(badge.classList.contains('ok'));
 w.document.getElementById('panel-home').hidden=true;poll();assert.equal(pending.length,1,'Header status must continue refreshing while a child section is visible');
 now+=16000;poll();assert.ok(!badge.classList.contains('ok'),'A pending slow request must not preserve old green');assert.match(badge.textContent,/قدیمی/);
 [...timers.values()].find(t=>t.ms===12000).fn();await flush();assert.ok(badge.classList.contains('bad'));assert.match(badge.title,/وضعیت خود Bybit نامشخص/);
 pending.shift();poll();await answer({},false,401);assert.match(badge.textContent,/ورود لازم/);assert.ok(!badge.classList.contains('ok'));
 poll();await answer({publicApiConnected:true,demoAuthenticated:true});assert.ok(badge.classList.contains('warn'),'Legacy booleans without fresh independent check evidence must never force green');
 poll();await answer(data('connected',{publicApi:{...check,ageSeconds:44}}));assert.ok(badge.classList.contains('ok'));now+=2000;poll();assert.ok(badge.classList.contains('warn'),'Exchange evidence must expire even before the browser transport deadline');await answer(data('unknown'));
 dom.window.close();console.log('PASS truthful connection UI: independent account/symbol states, read-only details, hidden panels, stale/timeout/session and unknown schema.');
})().catch(e=>{console.error(e);process.exitCode=1;});
