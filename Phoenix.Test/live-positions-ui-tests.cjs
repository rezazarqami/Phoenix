const assert=require('node:assert/strict'),fs=require('fs'),path=require('path');
const {JSDOM}=require('./ui/node_modules/jsdom');
(async()=>{
 const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),dom=new JSDOM(fs.readFileSync(path.join(root,'phoenix.html'),'utf8'),{url:'https://marketsignalist.ir/phoenix',runScripts:'outside-only'});
 const w=dom.window,d=w.document;let timer,requests=0,fail=false;w.setInterval=f=>{timer=f;return 1;};
 Object.defineProperty(d,'hidden',{value:false,configurable:true});
 w.fetch=async(url)=>{if(url==='/api/auth/me')return {ok:true,json:async()=>({isAdmin:true})};requests++;return {ok:!fail,status:fail?503:200,json:async()=>({updatedAtUtc:'2026-10-27T01:00:00Z',items:[
 {position:{symbol:'BTCUSDT<script>',side:'Buy',leverage:20,unrealisedPnl:5.25,returnPercent:50,averagePrice:100,markPrice:105,initialMargin:10,size:1},signalId:'one',firstProtection:true,timeframe:'15'},
 {position:{symbol:'ETHUSDT',side:'Sell',leverage:5,unrealisedPnl:-2.5,returnPercent:-25,averagePrice:100,size:2},signalId:'two',riskFreeReached:true},
 {position:{symbol:'UNKNOWN',side:'Buy',unrealisedPnl:0,returnPercent:null},matchedSignals:2}
 ]})};};
 w.eval(fs.readFileSync(path.join(root,'live-positions.js'),'utf8'));const flush=()=>new Promise(r=>setImmediate(r));
 timer();await flush();assert.equal(requests,0,'Hidden pane must not poll private exchange information');
 d.querySelector('#signal-pane-positions').hidden=false;w.dispatchEvent(new w.CustomEvent('phoenix-signal-tab-changed',{detail:'positions'}));await flush();
 assert.equal(d.querySelectorAll('.position-card').length,3);assert.ok(d.querySelector('.positive').textContent.includes('+5.25 USDT'));assert.ok(d.querySelector('.negative').textContent.includes('-25.00%'));assert.ok(d.querySelector('.position-stage').textContent.includes('پلهٔ اول'));assert.ok(d.querySelectorAll('.position-stage')[1].textContent.includes('پلهٔ دوم'));assert.equal(d.querySelector('.position-card script'),null);assert.ok(d.querySelectorAll('.position-stage')[2].textContent.includes('تجمیعی'));
 assert.equal(d.querySelector('.positions-heading p'),null);assert.equal(d.querySelector('.positions-note'),null);assert.ok(!d.querySelector('#positionsStatus').textContent.includes('۵ ثانیه'));assert.equal(d.querySelector('#positionsList').nextElementSibling.id,'positionsStatus');
 fail=true;timer();await flush();assert.equal(d.querySelectorAll('.position-card').length,3,'Network failure must retain the last snapshot');assert.ok(d.querySelector('#positionsStatus').textContent.includes('اطلاعات نمایش‌داده‌شده مربوط'));
 dom.window.close();console.log('PASS live positions: signed exchange values, protection stages, safe text, no hidden polling and stale-data status.');
})().catch(e=>{console.error(e);process.exitCode=1;});
