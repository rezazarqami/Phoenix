const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const root = path.join(__dirname, '../Phoenix.Web/wwwroot');
const html = fs.readFileSync(path.join(root, 'crypto-market.html'), 'utf8');
const nodes = new Map();
function add(id) {
  const listeners = {};
  const node = {value:'', textContent:'', innerHTML:'', disabled:false, hidden:false, checked:false,
    classList:{toggle(){}}, closest(){return this;},
    addEventListener(type, listener){listeners[type] = listener;},
    fire(type){return listeners[type]?.({target:this});},
    insertAdjacentHTML(_, fragment){for(const match of fragment.matchAll(/id="([^"]+)"/g)) add('#'+match[1]);}};
  nodes.set(id,node); return node;
}
for(const match of html.matchAll(/id="([^"]+)"/g)) add('#'+match[1]);
add('.results-report'); add('.shadow-signals');
for(const id of ['#batchDirection','#batchChart','#batchTimeframe']) nodes.get(id).value='All';
nodes.get('#marketFilter').value='all';nodes.get('#batchCount').value='20';nodes.get('#batchDuration').value='30';
const calls=[], timers=new Map();let serial=0;
const context={console,Intl,Date,URL,URLSearchParams,AbortController,
  document:{querySelector:selector=>nodes.get(selector) || null, querySelectorAll:()=>[]},
  localStorage:{getItem(){throw new Error('Storage denied');},setItem(){throw new Error('Storage denied');}},
  setTimeout(fn,delay){timers.set(++serial,{fn,delay});return serial;},clearTimeout(id){timers.delete(id);},
  location:{origin:'https://phoenix.test',replace(){}},
  fetch(url,options){let resolve,reject;const result=new Promise((yes,no)=>{resolve=yes;reject=no;});
    options.signal.addEventListener('abort',()=>reject(Object.assign(new Error('aborted'),{name:'AbortError'})));
    calls.push({url,options,resolve(data,ok=true){resolve({status:ok?200:409,ok,json:async()=>data});}});return result;}};
vm.createContext(context);
// Execute the actual two HTML script files, with no shared test-only formatter.
for(const file of ['crypto-market.js','signal-batch-controls.js']) vm.runInContext(fs.readFileSync(path.join(root,file),'utf8'),context);
const flush=()=>new Promise(resolve=>setImmediate(resolve));
(async()=>{
  assert.equal(calls.length,2,'Both market load and status must start with denied storage');
  const assets=Array.from({length:700},(_,i)=>({symbol:`COIN${i}USDT`,baseSymbol:`COIN${i}`,name:`Coin ${i}`,activeCount:i<10?1:0,activeLong:i<10?1:0,activeShort:0}));
  calls.find(c=>c.url==='/api/analysis/coins').resolve({assets});
  calls.find(c=>c.url==='/api/analysis/signal-batch').resolve({running:false,message:'صفی فعال نیست.'});
  await flush();
  assert.equal(nodes.get('#assetCount').textContent,new Intl.NumberFormat('fa-IR').format(700));
  assert.equal(nodes.get('#activeCount').textContent,new Intl.NumberFormat('fa-IR').format(10));
  assert.equal(nodes.get('#freeCount').textContent,new Intl.NumberFormat('fa-IR').format(690));
  const rowCount=()=> (nodes.get('#marketRows').innerHTML.match(/class="market-row"/g)||[]).length;
  assert.equal(rowCount(),50,'Initial DOM must be bounded');
  nodes.get('#marketMore').fire('click');assert.equal(rowCount(),100);
  nodes.get('#marketSearch').value='COIN699';nodes.get('#marketSearch').fire('input');
  [...timers.values()].find(t=>t.delay===150).fn();
  assert.equal(rowCount(),1,'Search must include assets beyond the rendered slice');
  const start=nodes.get('#startBatch').fire('click');
  assert.match(nodes.get('#batchStatus').textContent,/شروع/);
  const post=calls.at(-1);assert.equal(post.options.method,'POST');
  assert.equal(JSON.parse(post.options.body).positionSizeUsdt,10);
  post.resolve({error:'ربات تلگرام تنظیم نشده است.'},false);await flush();
  assert.match(nodes.get('#batchError').textContent,/ربات تلگرام/,'Failure must remain visible beside the button');
  calls.at(-1).resolve({running:false,message:'صفی فعال نیست.'});await start;
  assert.equal(nodes.get('#startBatch').disabled,false);
  assert.match(nodes.get('#batchError').textContent,/ربات تلگرام/,'Polling must not hide the command failure');
  console.log('PASS full Crypto Markets initialization: denied storage, all three counters, bounded rows, full-list search and inline start failure.');
})().catch(error=>{console.error(error);process.exitCode=1;});
