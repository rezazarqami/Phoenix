const assert=require('node:assert/strict'),fs=require('fs'),path=require('path');
const {JSDOM}=require('./ui/node_modules/jsdom');
(async()=>{
 const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),dom=new JSDOM(fs.readFileSync(path.join(root,'crypto-market.html'),'utf8'),{url:'https://fixture.test/analysis/coins',runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window,d=w.document;let tick,active=2;w.setInterval=f=>{tick=f;return 0;};w.scrollTo=()=>{};
 const proposal=(key,symbol)=>({key,symbol,caption:'<script>unsafe</script>\nورود: 100',image:'/fixture.png',state:'Ready'});
 let items=[proposal('one','BTCUSDT'),proposal('two','ETHUSDT'),proposal('three','SOLUSDT')],posts=[],releases=new Map();
 w.fetch=async(url,options)=>{
  if(url==='/api/analysis/proposals/remove'){const keys=JSON.parse(options.body).keys;items=items.filter(x=>!keys.includes(x.key));return {ok:true};}
  if(options?.method==='POST'){const key=url.split('/').at(-2);posts.push({key,...JSON.parse(options.body)});await new Promise(r=>releases.set(key,r));items=items.filter(x=>x.key!==key);if(key==='one')active++;return {ok:true};}
  return {ok:true,json:async()=>url==='/api/signals'?Array.from({length:active},()=>({status:'Pending'})):JSON.parse(JSON.stringify(items))};
 };
 w.eval(fs.readFileSync(path.join(root,'in-app-review.js'),'utf8'));const flush=()=>new Promise(r=>setImmediate(r));await flush();
 assert.equal(d.querySelector('#reviewBadge').textContent,'۳');assert.equal(d.querySelector('#reviewDetail script'),null);assert.match(d.querySelector('.review-details-dialog pre').textContent,/<script>/);
 d.querySelector('#reviewDetail textarea').value='draft';tick();await flush();assert.equal(d.querySelector('#reviewDetail textarea').value,'draft');
 const old=d.querySelector('.review-actions button');old.click();old.click();await flush();assert.equal(posts.length,1,'Same stale button cannot submit twice');assert.match(d.querySelector('.review-title').textContent,/ETHUSDT/,'Advance without waiting for POST');assert.equal(d.querySelector('.review-actions button').disabled,false);
 d.querySelectorAll('.review-actions button')[2].click();await flush();assert.equal(posts.length,2);assert.match(d.querySelector('.review-title').textContent,/SOLUSDT/,'Next rejection also advances immediately');tick();await flush();assert.equal(d.querySelectorAll('#reviewList button').length,1,'Polling cannot resurrect pending or completed choices');
 releases.get('two')();await flush();await flush();assert.match(d.querySelector('.review-title').textContent,/SOLUSDT/,'Out-of-order completion preserves current proposal');
 releases.get('one')();await flush();await flush();assert.equal(d.querySelector('#activeCount').textContent,'۳','Counter refreshes after acknowledged registration');assert.match(d.querySelector('.review-title').textContent,/SOLUSDT/);
 assert.match(fs.readFileSync(path.join(root,'in-app-review.css'),'utf8'),/aspect-ratio:4\/3/,'Reserve chart space before image loads');
 items=[{...proposal('stuck','AKEUSDT'),state:'NeedsReview',error:'failed'},proposal('fresh','GASUSDT')];await tick();await flush();
 assert.match(d.querySelector('.review-title').textContent,/AKEUSDT/);assert.equal(d.querySelector('.review-actions'),null);
 w.confirm=()=>false;d.querySelector('#reviewDetail .review-remove').click();await flush();assert.equal(items.length,2,'Cancelled delete preserves desk');
 w.confirm=()=>true;d.querySelector('#reviewDetail .review-remove').click();await flush();await flush();assert.equal(items.length,1,'Stuck proposal can be removed independently');
 d.querySelector('#clearReviewDesk').click();await flush();await flush();assert.equal(items.length,0,'Clear desk removes current snapshot');assert.equal(d.querySelectorAll('#reviewList button').length,0);
 dom.window.close();console.log('PASS review: immediate next proposal, concurrent choices, stale-click exclusion, polling isolation and live active counter.');
})().catch(e=>{console.error(e);process.exitCode=1;});
