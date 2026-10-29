const assert=require('node:assert/strict'),fs=require('fs'),path=require('path');const {JSDOM}=require('./ui/node_modules/jsdom');
(async()=>{
 const base=path.join(__dirname,'../Phoenix.Web/wwwroot'),dom=new JSDOM(fs.readFileSync(path.join(base,'phoenix.html'),'utf8'),{url:'https://marketsignalist.ir/phoenix',runScripts:'outside-only'}),w=dom.window,d=w.document;
 let posts=[],mounts=0,stops=0,confirmed=true;
 w.phoenixConfirm=async()=>confirmed;w.refreshSignals=async()=>{};w.refreshHistory=async()=>{};w.PhoenixLiveChart={mount(){mounts++;return {stop(){stops++;}};}};
 w.fetch=async(url,options)=>url==='/api/signals/timeframe-rules'?{ok:true,json:async()=>[]}:url==='/api/auth/me'?{json:async()=>({isAdmin:true})}:{ok:true,json:async()=>{posts.push(JSON.parse(options.body).ids);return {cancelled:1,items:[{id:'three',cancelled:true}]};}};
 w.eval(fs.readFileSync(path.join(base,'active-signals.js'),'utf8'));const flush=()=>new Promise(r=>setImmediate(r));await flush();
 let signals=[{id:'one',symbol:'BTCUSDT<script>',direction:'Long',status:'Pending',entryPrice:100},{id:'two',symbol:'ETHUSDT',direction:'Short',status:'Filled',filledAtUtc:'2026-01-01'},{id:'three',symbol:'SOLUSDT',direction:'Long',status:'Submitted'},{id:'four',symbol:'QNTUSDT',direction:'Long',status:'Submitting'}];
 const send=()=>w.dispatchEvent(new w.CustomEvent('phoenix-signals-updated',{detail:signals}));send();assert.equal(d.querySelectorAll('.active-signal-row').length,4);assert.equal(d.querySelector('#orders script'),null);assert.equal(d.querySelector('#signal-pane-active #closePositionsButton'),null);assert.ok(d.querySelector('#signal-pane-positions #closePositionsButton'));
 d.querySelector('#selectAllSignals').click();assert.equal(d.querySelectorAll('.active-signal-check:checked').length,2,'Select all must only include unfilled pending/submitted signals');
 d.querySelector('.active-expand').click();assert.equal(mounts,1);send();assert.equal(mounts,1,'Refreshing must retain the same expanded chart');assert.equal(d.querySelector('.active-signal-detail').hidden,false);
 signals=signals.map(s=>s.id==='one'?{...s,status:'Filled',filledAtUtc:'2026-01-01'}:s);send();assert.equal(d.querySelectorAll('.active-signal-check:checked').length,1,'Signals filled while selected must be removed from selection');
 confirmed=false;d.querySelector('#cancelSelectedSignals').click();await flush();assert.equal(posts.length,0);confirmed=true;d.querySelector('#cancelSelectedSignals').click();await flush();await flush();assert.deepEqual(posts,[['three']]);
 signals=signals.filter(s=>s.id!=='one');send();assert.equal(stops,1,'Removing an expanded row must dispose its chart');assert.equal(d.querySelector('#cancelDirection'),null,'Direction bulk panel must be removed');
 dom.window.close();console.log('PASS active desk: eligible selection, fill-race pruning, confirmation, safe text, chart persistence and position-control relocation.');
})().catch(e=>{console.error(e);process.exitCode=1;});
