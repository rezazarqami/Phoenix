const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {JSDOM,VirtualConsole}=require('./ui/node_modules/jsdom');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot');
const read=file=>fs.readFileSync(path.join(root,file),'utf8');
const flush=()=>new Promise(r=>setImmediate(r));
(async()=>{
 const dom=new JSDOM(read('crypto-market.html'),{url:'https://phoenix.test/analysis/coins',runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window,d=w.document;w.setTimeout=()=>0;w.setInterval=()=>0;
 w.fetch=async url=>({ok:true,status:200,json:async()=>url.includes('shadow')?{summary:{total:0},signals:[]}:url.includes('learning')?{proposalSamples:1,entrySamples:1}:url.includes('coins')?{assets:[]}:{running:false,message:'صفی فعال نیست.'}});
 for(const file of ['crypto-market.js','signal-batch-controls.js','crypto-ice.js'])w.eval(read(file));
 await flush();
 const ids=[...d.querySelectorAll('[id]')].map(x=>x.id);assert.equal(new Set(ids).size,ids.length);
 assert.equal(d.querySelectorAll('[data-market-tab]').length,6);assert.equal(d.querySelector('#freeCount'),null);
 const visible=()=>[...d.querySelectorAll('main [role=tabpanel]')].filter(p=>!p.hidden).map(p=>p.id);
 d.querySelector('#batchSize').value='42';
 for(const key of ['create','approved','history','archive','statistics','search']){d.querySelector(`[data-market-tab=${key}]`).click();assert.deepEqual(visible(),['market-pane-'+key]);}
 d.querySelector('#evaluationFrom').value='2026-01-01';d.querySelector('[data-market-tab=create]').click();assert.equal(d.querySelector('#batchSize').value,'42');
 d.querySelector('[data-market-tab=history]').click();assert.equal(d.querySelector('#evaluationFrom').value,'2026-01-01');
 d.querySelector('[data-market-tab=create]').dispatchEvent(new w.KeyboardEvent('keydown',{key:'End',bubbles:true}));assert.deepEqual(visible(),['market-pane-search']);assert.equal(d.activeElement.id,'market-tab-search');
 const count=d.querySelector('#batchCount');count.value='1';count.closest('label').querySelector('button').click();assert.equal(count.value,'1','Stepper obeys min');
 count.value='200';count.closest('label').querySelectorAll('button')[1].click();assert.equal(count.value,'200','Stepper obeys max');
 const timed=d.querySelector('#batchTimedMode');timed.checked=true;timed.dispatchEvent(new w.Event('change'));await flush();assert.ok(count.disabled);assert.equal(d.querySelector('#batchDuration').disabled,false);assert.ok(count.closest('label').querySelector('button').disabled);assert.ok(d.querySelector('#startBatch svg'),'Mode changes preserve icon');
 dom.window.close();
 for(const native of [false,true]){
  const html=native?fs.readFileSync(path.join(__dirname,'../android/app/src/main/assets/phoenix-login.html'),'utf8'):read('login.html');
  assert.doesNotMatch(html,/مدار|orbit/i);assert.match(html,/phoenix-ice|login-ice/);
  const console=new VirtualConsole();console.on('jsdomError',err=>{if(!err.message.includes('navigation'))throw err;});
  const login=new JSDOM(html,{url:'https://phoenix.test/login',runScripts:'outside-only',virtualConsole:console});const lw=login.window,ld=lw.document;const requests=[];let accepted=false;
  lw.fetch=async(url,options)=>{requests.push({url,options});return {ok:accepted,status:accepted?200:401,json:async()=>accepted?{isAdmin:true}:{error:'ورود ناموفق بود.'}};};
  lw.eval(native?ld.querySelector('script').textContent:read('login.js'));
  ld.querySelector('#username').value='fixture';ld.querySelector('#password').value='test-password';ld.querySelector('#togglePassword').click();assert.equal(ld.querySelector('#password').type,'text');
  ld.querySelector('#loginForm').dispatchEvent(new lw.Event('submit',{cancelable:true,bubbles:true}));await flush();assert.match(ld.querySelector('#loginMessage').textContent,/ناموفق/);assert.equal(ld.querySelector('.submit').disabled,false);
  assert.equal(requests[0].url,'/api/auth/login');assert.equal(requests[0].options.credentials,'same-origin');assert.deepEqual(JSON.parse(requests[0].options.body),{username:'fixture',password:'test-password'});
  accepted=true;ld.querySelector('#loginForm').dispatchEvent(new lw.Event('submit',{cancelable:true,bubbles:true}));await flush();assert.equal(ld.querySelector('#password').value,'');assert.equal(ld.querySelector('#loginMessage').textContent,'');login.window.close();
 }
 console.log('PASS ice UI: six tabs, persistent inputs, bounded steppers, timed mode, password visibility and real web/native login submission.');
})().catch(e=>{console.error(e);process.exitCode=1;});
