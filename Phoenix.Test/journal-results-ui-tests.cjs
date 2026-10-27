const assert=require('node:assert/strict'),fs=require('fs'),path=require('path');const {JSDOM}=require('./ui/node_modules/jsdom');
(async()=>{
 const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),dom=new JSDOM(fs.readFileSync(path.join(root,'notifications.html'),'utf8'),{url:'https://marketsignalist.ir/notifications',runScripts:'outside-only'});
 const w=dom.window,d=w.document;w.setInterval=()=>0;const requests=[];let resolveOlder,importPayload,recovered=false;
 const event=(kind,sequence,wallet)=>({id:sequence+':'+kind,kind,sequence,symbol:'BTCUSDT',direction:'Long',at:'2026-01-'+String(sequence).padStart(2,'0')+'T00:00:00Z',walletBalanceUsdt:wallet});
 w.fetch=async(url,options)=>{
  requests.push(url);
  if(url.endsWith('/wallet-recovery'))return {ok:true,json:async()=>({state:'waiting',message:'آماده'})};
  if(url.endsWith('/recover-wallets')){recovered=true;return {ok:true,json:async()=>({state:'completed',recovered:1,unresolved:0,atUtc:'2026-10-27T00:00:00Z',message:'بازیابی انجام شد'})};}
  if(options?.method==='POST'){importPayload=JSON.parse(options.body);return {ok:true,json:async()=>({imported:2,unresolved:3})};}
  const results=url.includes('view=results'),older=url.includes('before=');
  if(older&&!results)return await new Promise(r=>{resolveOlder=()=>r({ok:true,json:async()=>({events:[event('NearEntry',1)],before:1,hasMore:false,focusFound:true})});});
  return {ok:true,json:async()=>({events:results?(older?[event('Target',2,0)]:[{...event('Target',6,recovered?35.5:20),...(recovered?{walletSource:'BybitTransaction',walletAtUtc:'2026-10-27T00:00:00Z'}:{})},event('RiskFree',5,25),event('StopLoss',4,null)]):[event('Opened',3),event('RiskFreeReached',4),{...event('Target',6,recovered?35.5:20),...(recovered?{walletSource:'BybitTransaction',walletAtUtc:'2026-10-27T00:00:00Z'}:{})}],before:results?4:3,hasMore:!older,focusFound:true})};
 };
 w.eval(fs.readFileSync(path.join(root,'notifications.js'),'utf8'));const flush=()=>new Promise(r=>setImmediate(r));await flush();assert.equal(d.querySelectorAll('.event-card').length,3);
 d.querySelector('#loadOlder').click();await flush();d.querySelector('#journal-tab-results').click();await flush();
 assert.equal(d.querySelector('#journal-tab-results').getAttribute('aria-selected'),'true');assert.equal(d.querySelectorAll('.event-card').length,3);assert.ok([...d.querySelectorAll('.event-card')].every(c=>['Target','RiskFree','StopLoss'].includes(c.dataset.kind)));assert.equal(d.querySelectorAll('.event-wallet').length,3);assert.deepEqual([...d.querySelectorAll('.event-kind')].map(x=>x.textContent),['TP','RF','SL']);assert.ok([...d.querySelectorAll('.event-detail')].every(x=>!x.open),'Details start collapsed');assert.ok([...d.querySelectorAll('.event-card')].every(c=>c.lastElementChild.classList.contains('event-wallet')),'Every final card must end with its wallet row');
 resolveOlder();await flush();assert.equal(d.querySelectorAll('.event-card').length,3,'Late all-events response must not replace the result view');
 d.querySelector('#loadOlder').click();await flush();assert.equal(d.querySelectorAll('.event-card').length,4);assert.ok(d.querySelectorAll('.event-wallet')[3].textContent.includes('0 USDT'));
 d.querySelector('#journal-tab-all').click();await flush();assert.ok([...d.querySelectorAll('.event-card')].some(c=>c.dataset.kind==='Opened'),'Switching tabs must restore all events');
 d.querySelector('#recoverBybitWallets').click();await flush();await flush();assert.ok(d.querySelector('#bybitRecoveryStatus').textContent.includes('بازیابی انجام شد'));assert.ok(d.querySelector('.event-wallet').textContent.includes('35.5 USDT'));assert.ok(d.querySelector('.event-card .wallet-source').textContent.includes('پس از تسویه در بای‌بیت'));
 const file={size:100,text:async()=>JSON.stringify({messages:[{id:1,text:'fixture'}]})};Object.defineProperty(d.querySelector('#walletImportFile'),'files',{value:[file]});d.querySelector('#importWallets').click();await flush();await flush();assert.deepEqual(importPayload.export.messages,[{id:1,text:'fixture'}]);assert.ok(d.querySelector('#walletImportStatus').textContent.includes('بازیابی شد'));
 assert.ok(d.querySelector('.journal-brand img').src.endsWith('phoenix-ice-logo.png'));assert.ok(requests.some(x=>x.includes('view=results')&&x.includes('before=4')));
 dom.window.close();console.log('PASS journal: independent result pagination, stale-response isolation, final-only cards, wallet footers and Telegram file import.');
})().catch(e=>{console.error(e);process.exitCode=1;});
