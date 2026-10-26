const assert=require('node:assert/strict'),fs=require('fs'),path=require('path');
const {JSDOM}=require('./ui/node_modules/jsdom');
(async()=>{
 const root=path.join(__dirname,'../Phoenix.Web/wwwroot');
 const dom=new JSDOM(fs.readFileSync(path.join(root,'crypto-market.html'),'utf8'),{url:'https://fixture.test/analysis/coins',runScripts:'outside-only'});
 const w=dom.window,d=w.document;w.setInterval=()=>0;
 let items=[{key:'one',symbol:'BTCUSDT',caption:'<script>unsafe</script>\nورود: 100',image:'/fixture.png',createdAt:'2026-01-01T00:00:00Z',state:'Ready'}];
 let release,posts=[];w.fetch=async(url,options)=>{
  if(options?.method==='POST'){posts.push(JSON.parse(options.body));await new Promise(r=>release=r);items=[];return {ok:true};}
  return {ok:true,json:async()=>items};
 };
 w.eval(fs.readFileSync(path.join(root,'in-app-review.js'),'utf8'));
 const flush=()=>new Promise(r=>setImmediate(r));await flush();
 assert.equal(d.querySelector('#reviewBadge').textContent,'۱');
 assert.ok(d.querySelector('#reviewDetail pre').textContent.includes('<script>'));assert.equal(d.querySelector('#reviewDetail script'),null);
 d.querySelector('#reviewDetail textarea').value='دلیل آزمایشی';
 const buttons=d.querySelectorAll('.review-actions button');buttons[2].click();buttons[2].click();await flush();
 assert.equal(posts.length,1);assert.deepEqual(posts[0],{action:'reject',reason:'دلیل آزمایشی'});
 assert.ok(buttons[0].disabled);release();await flush();await flush();
 assert.equal(d.querySelector('#reviewBadge').textContent,'۰');assert.equal(d.querySelector('.review-actions'),null);
 dom.window.close();console.log('PASS in-app review: safe text, durable API action, duplicate click exclusion and next proposal refresh.');
})().catch(e=>{console.error(e);process.exitCode=1;});
