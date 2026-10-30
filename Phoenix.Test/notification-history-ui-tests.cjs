const assert=require('node:assert/strict'),fs=require('fs'),path=require('path');
const {JSDOM}=require('./ui/node_modules/jsdom');
(async()=>{
 const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),dom=new JSDOM(fs.readFileSync(path.join(root,'notifications.html'),'utf8'),{url:'https://marketsignalist.ir/notifications?eventId=one%3ARiskFree',runScripts:'outside-only'});
 const w=dom.window,d=w.document;w.setInterval=()=>0;w.scrollBy=()=>{};const requests=[];let refreshed=false;
 const selected={sequence:9,id:'one:RiskFree',kind:'RiskFree',symbol:'BTCUSDT<script>',direction:'Long',at:'2026-01-03T00:00:00Z',exitKind:'SecondStepLimit',entryPrice:100,takeProfit:110,stopLoss:90,walletBalanceUsdt:123.45};
 w.fetch=async url=>{requests.push(url);const old=url.includes('before=8');return {ok:true,json:async()=>({events:refreshed?[{...selected,sequence:10,id:'new:Target',kind:'Target',at:'2026-01-04T00:00:00Z',walletBalanceUsdt:0}]:old?[{...selected,sequence:2,id:'older:Target',kind:'Target',at:'2026-01-01T00:00:00Z'}]:[{...selected,sequence:8,id:'before:Opened',kind:'Opened',at:'2026-01-02T00:00:00Z'},selected],before:old?2:8,hasMore:!old,focusFound:true})};};
 w.eval(fs.readFileSync(path.join(root,'notifications.js'),'utf8'));const flush=()=>new Promise(r=>setImmediate(r));await flush();
 assert.ok(requests[0].includes('eventId=one%3ARiskFree'));assert.equal(d.querySelectorAll('.event-card').length,2);assert.equal(d.querySelector('.focused').dataset.eventId,'one:RiskFree');assert.ok(d.querySelector('.focused').textContent.includes('پلهٔ دوم'));assert.equal(d.querySelector('.focused .event-kind').textContent,'RF2');assert.equal(d.querySelector('.focused .event-message').textContent,'معامله با پلهٔ دوم ریسک‌فری بسته شد.');assert.equal(d.querySelector('.focused .event-detail').open,false);d.querySelector('.focused .event-detail').open=true;await flush();assert.equal(d.querySelectorAll('script:not([src])').length,0);
 d.querySelector('#loadOlder').click();await flush();assert.equal(d.querySelectorAll('.event-card').length,3);assert.equal(d.querySelector('.event-card').dataset.eventId,'one:RiskFree');assert.equal(d.querySelectorAll('.event-card')[2].dataset.eventId,'older:Target');assert.ok(d.querySelector('.event-wallet').textContent.includes('123.45 USDT'));assert.equal(d.querySelectorAll('.event-wallet').length,2);assert.equal(d.querySelector('#loadOlder').hidden,true);assert.equal(d.querySelector('.focused .event-detail').open,true,'Opening older pages must preserve expanded details');
 d.querySelector('#refreshEvents').click();await flush();assert.equal(d.querySelectorAll('.event-card').length,3,'Refreshing must not duplicate or discard older cards');refreshed=true;d.querySelector('#refreshEvents').click();await flush();assert.equal(d.querySelector('.event-card').dataset.eventId,'new:Target','New events must appear at the top');assert.ok(d.querySelector('.event-wallet').textContent.includes('0 USDT'));dom.window.close();
 // Activation labels and exit labels must remain distinct, including old generic reasons.
 const stageDom=new JSDOM(fs.readFileSync(path.join(root,'notifications.html'),'utf8'),{url:'https://marketsignalist.ir/notifications',runScripts:'outside-only'});
 const sw=stageDom.window,sd=sw.document;sw.setInterval=()=>0;
 const events=[
  {...selected,id:'first:FirstProtection',kind:'FirstProtection',exitKind:null,reason:'پیام قدیمی'},
  {...selected,id:'second:RiskFreeReached',kind:'RiskFreeReached',exitKind:null,reason:'معامله به محدودهٔ ریسک‌فری رسید.'},
  {...selected,id:'close1:RiskFree',kind:'RiskFree',exitKind:'FirstStepLimit'},
  {...selected,id:'close2:RiskFree',kind:'RiskFree',exitKind:'SecondStepLimit'},
  {...selected,id:'unknown:RiskFree',kind:'RiskFree',exitKind:null},
  {...selected,id:'target:Target',kind:'Target'}
 ];
 sw.fetch=async()=>({ok:true,json:async()=>({events,hasMore:false})});
 const style=sd.createElement('style');style.textContent=fs.readFileSync(path.join(root,'notifications.css'),'utf8');sd.head.append(style);
 sw.eval(fs.readFileSync(path.join(root,'notifications.js'),'utf8'));await flush();
 const card=id=>sd.querySelector(`[data-event-id="${id}"]`);
 for(const [id,badge,message] of [
  ['first:FirstProtection','RF1','پلهٔ اول ریسک‌فری فعال شد.'],
  ['second:RiskFreeReached','RF2','پلهٔ دوم ریسک‌فری فعال شد.'],
  ['close1:RiskFree','RF1','معامله با پلهٔ اول ریسک‌فری بسته شد.'],
  ['close2:RiskFree','RF2','معامله با پلهٔ دوم ریسک‌فری بسته شد.'],
  ['unknown:RiskFree','RF','معامله با ریسک‌فری بسته شد.']
 ]){assert.equal(card(id).querySelector('.event-kind').textContent,badge);assert.equal(card(id).querySelector('.event-message').textContent,message);}
 assert.equal(sw.getComputedStyle(card('target:Target').querySelector('.event-kind')).color,'rgb(36, 224, 90)');
 for(const e of events.filter(e=>e.kind!=='Target'))assert.equal(sw.getComputedStyle(card(e.id).querySelector('.event-kind')).color,'rgb(34, 211, 238)');
 stageDom.window.close();
 console.log('PASS event history: selected event, chronological pages, risk-free label, safe text and deduplication.');
})().catch(e=>{console.error(e);process.exitCode=1;});
