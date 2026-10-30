const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM}=require('./ui/node_modules/jsdom');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),script=fs.readFileSync(path.join(root,'notifications.js'),'utf8'),html=fs.readFileSync(path.join(root,'notifications.html'),'utf8');
const flush=()=>new Promise(r=>setImmediate(r));
const event={sequence:9,id:'one:Target',kind:'Target',symbol:'BTCUSDT',direction:'Long',at:'2026-10-30T01:00:00Z',walletBalanceUsdt:12};
function page(user,storage,handler){
 const dom=new JSDOM(html,{url:'https://phoenix.test/notifications?view=results',runScripts:'outside-only'}),w=dom.window,intervals=[];
 Object.defineProperty(w.document,'hidden',{value:false,configurable:true});
 w.setInterval=(fn,ms)=>{intervals.push({fn,ms});return 0;};
 if(storage)w.localStorage.setItem('phoenix-journal-v1:alice',storage);
 w.fetch=async(url,options)=>url==='/api/auth/me'?{ok:true,json:async()=>({username:user})}:url.includes('wallet-recovery')?{ok:true,json:async()=>({state:'waiting'})}:handler(url,options);
 w.eval(script);return {dom,w,intervals};
}
(async()=>{
 let requests=0;
 const first=page('alice',null,async()=>({ok:true,headers:{get:()=> '"v1"'},json:async()=>({events:[event],before:9,hasMore:true,focusFound:true})}));
 await flush();const saved=first.w.localStorage.getItem('phoenix-journal-v1:alice');assert.ok(saved);first.dom.window.close();
 let release;
 const second=page('alice',saved,async(url,options)=>{requests++;assert.equal(options.headers['If-None-Match'],'"v1"');return new Promise(r=>{release=r;});});
 await flush();assert.match(second.w.document.querySelector('#eventTimeline').textContent,/BTCUSDT/,'Cached history must render while network response is pending');
 assert.match(second.w.document.querySelector('.event-wallet').textContent,/12 USDT/);
 const original=second.w.document.querySelector('.event-card');release({status:304});await flush();assert.equal(second.w.document.querySelector('.event-card'),original,'304 must preserve the rendered card');
 assert.equal(requests,1);
 Object.defineProperty(second.w.document,'hidden',{value:true});second.intervals.find(x=>x.ms===5000).fn();assert.equal(requests,1,'Hidden pages must not poll');second.dom.window.close();
 const other=page('bob',saved,async()=>new Promise(()=>{}));await flush();assert.doesNotMatch(other.w.document.querySelector('#eventTimeline').textContent,/BTCUSDT/,'Another account cannot restore Alice history');other.dom.window.close();
 const updated=page('alice',saved,async()=>({ok:true,headers:{get:()=> '"v2"'},json:async()=>({events:[{...event,walletBalanceUsdt:18}],before:9,hasMore:true,focusFound:true})}));
 await flush();assert.match(updated.w.document.querySelector('.event-wallet').textContent,/18 USDT/,'Backfilled wallet changes must replace cached values');updated.dom.window.close();
 const denied=page('alice',saved,async()=>({status:401,ok:false}));await flush();assert.equal(denied.w.document.querySelectorAll('.event-card').length,0);assert.equal(denied.w.localStorage.getItem('phoenix-journal-v1:alice'),null);denied.dom.window.close();
 console.log('PASS cached journal: instant restore, account isolation, conditional refresh, wallet updates, hidden polling and expired session cleanup.');
})().catch(e=>{console.error(e);process.exitCode=1;});
