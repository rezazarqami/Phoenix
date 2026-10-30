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
let downloads=0, lastDownload;
const fakeURL={createObjectURL(){return 'blob:test';},revokeObjectURL(){}};
const context={navigator:{clipboard:{async writeText(text){context.copied=text;}}},console,Intl,Date,URL:fakeURL,URLSearchParams,AbortController,
  document:{body:{appendChild(){}},createElement(){return {click(){downloads++;lastDownload=this.href;},remove(){}};},querySelector:selector=>nodes.get(selector) || null, querySelectorAll:()=>[]},
  localStorage:{getItem(){throw new Error('Storage denied');},setItem(){throw new Error('Storage denied');}},
  setTimeout(fn,delay){timers.set(++serial,{fn,delay});return serial;},clearTimeout(id){timers.delete(id);},
  location:{origin:'https://phoenix.test',replace(){}},
  fetch(url,options){let resolve,reject;const result=new Promise((yes,no)=>{resolve=yes;reject=no;});
    options.signal.addEventListener('abort',()=>reject(Object.assign(new Error('aborted'),{name:'AbortError'})));
    calls.push({url,options,resolve(data,ok=true){resolve({status:ok?200:409,ok,headers:{get:name=>name.toLowerCase()==='content-type'?'application/json':null},blob:async()=>({}),json:async()=>data});}});return result;}};
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
  assert.equal(nodes.has('#freeCount'),false,'Only the two requested counters remain');
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
  const exportButton=nodes.get('#exportEvaluation');
  await exportButton.fire('click');
  assert.equal(downloads,1);assert.match(lastDownload,/^\/api\/analysis\/evaluation\/export\?/);
  assert.doesNotMatch(lastDownload,/blob:/);
  assert.doesNotMatch(nodes.get('#evaluationMessage').textContent,/دانلود آغاز شد/);
  const show=nodes.get('#showEvaluation');
  const task=show.fire('click');assert.equal(show.disabled,true);
  assert.match(calls.at(-1).url,/format=json/);
  const count=calls.length;await show.fire('click');assert.equal(calls.length,count);
  calls.at(-1).resolve({rows:[{id:'one'}],total:101,offset:0,count:100,nextOffset:100});await task;
  assert.equal(show.disabled,false);assert.equal(nodes.get('#evaluationOutput').hidden,false);
  assert.equal(nodes.get('#nextEvaluation').hidden,false);
  const next=nodes.get('#nextEvaluation').fire('click');assert.match(calls.at(-1).url,/offset=100/);
  calls.at(-1).resolve({rows:[{id:'last'}],total:101,offset:100,count:1,nextOffset:null});await next;
  assert.equal(nodes.get('#nextEvaluation').hidden,true);
  await nodes.get('#copyEvaluation').fire('click');assert.match(context.copied,/last/);
  const failed=show.fire('click');calls.at(-1).resolve({error:'بازه را کوتاه کنید'},false);
  await failed;assert.match(nodes.get('#evaluationMessage').textContent,/بازه را کوتاه/);
  assert.match(nodes.get('#evaluationText').value,/last/,'Failed page fetch preserves prior evidence');
  const timed=show.fire('click');[...timers.values()].find(t=>t.delay===120000).fn();await timed;
  assert.equal(show.disabled,false);assert.match(nodes.get('#evaluationMessage').textContent,/طول کشید/);
  console.log('PASS full Crypto Markets initialization: denied storage, two market counters, bounded rows, full-list search and inline start failure.');
})().catch(error=>{console.error(error);process.exitCode=1;});
