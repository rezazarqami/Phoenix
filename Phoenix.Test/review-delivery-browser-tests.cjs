const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const {chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium');
(async()=>{
 const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),hits={},posts=[],releases=[];
 let failD=true;
 const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRu4AAAAASUVORK5CYII=','base64');
 let items=['a','b','c','d'].map(key=>({key,symbol:key.toUpperCase()+'USDT',caption:'ورود: 100',image:'/chart/'+key,state:'Ready'}));
 const server=http.createServer((req,res)=>{
  const url=req.url;hits[url]=(hits[url]||0)+1;
  if(url==='/'){res.setHeader('Content-Type','text/html');return res.end('<!doctype html><body><div id="market-pane-review"><div id="reviewList"></div><div id="reviewDetail"></div><span id="reviewStatus"></span><span id="reviewBadge"></span><span id="reviewCapacity"></span><span id="activeCount"></span><button id="closeReviewFocus"></button><button id="market-tab-review"></button><button id="startBatch"></button><button id="restoreReviewDesk"></button><button id="clearReviewDesk"></button></div>');}
  if(url==='/api/analysis/proposals')return res.end(JSON.stringify(items));
  if(url==='/api/signals/active-count')return res.end('{"count":2}');
  if(req.method==='POST'){const key=url.split('/').at(-2);posts.push(key);return releases.push(()=>{items=items.filter(i=>i.key!==key);res.end('{}');});}
  if(url.startsWith('/chart/')){if(url==='/chart/d'&&failD){res.writeHead(503);return res.end();}return setTimeout(()=>{res.setHeader('Content-Type','image/png');res.end(png);},url==='/chart/a'?150:400);}
  res.writeHead(404).end();
 });
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});
 try{
  const page=await browser.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port);
  for(const name of ['review-images.js','in-app-review.js'])await page.addScriptTag({content:fs.readFileSync(path.join(root,name),'utf8')});
  await page.waitForFunction(()=>document.querySelector('#reviewDetail img')?.naturalWidth>0);
  await page.locator('.review-actions button').first().click();await page.waitForFunction(()=>document.querySelector('.review-title')?.textContent.includes('BUSDT'));
  await page.waitForFunction(()=>document.querySelector('#reviewDetail img')?.naturalWidth>0);assert.equal(hits['/chart/b'],1,'Next chart must reuse prefetch');assert.deepEqual(posts,['a'],'Chart work cannot duplicate approval');
  await page.locator('.review-actions button').nth(1).click();await page.waitForFunction(()=>document.querySelector('.review-title')?.textContent.includes('CUSDT'));
  await page.waitForFunction(()=>document.querySelector('#reviewDetail img')?.naturalWidth>0);
  await page.getByRole('button',{name:'DUSDT',exact:true}).click();await page.getByRole('button',{name:'دریافت دوبارهٔ نمودار',exact:true}).waitFor({state:'visible'});
  const failedAttempts=hits['/chart/d'];failD=false;await page.evaluate(()=>window.dispatchEvent(new Event('online')));await page.waitForFunction(()=>document.querySelector('#reviewDetail img')?.naturalWidth>0);
  assert.equal(hits['/chart/d'],failedAttempts+1,'Returning online recovers a failed chart');assert.equal(hits['/api/signals']||0,0,'Review never requests the expensive signals endpoint');
  releases.splice(0).reverse().forEach(r=>r());await page.waitForFunction(()=>!document.querySelector('#reviewStatus').textContent.includes('پاسخ در حال ثبت'));
  assert.match(await page.locator('.review-title').textContent(),/DUSDT/,'Out-of-order POST acknowledgements preserve the selected chart');assert.deepEqual(errors,[]);
  console.log('PASS browser review delivery: decoded prefetched PNGs, delayed concurrent decisions, failed-chart recovery and no heavy counter request.');
 }finally{releases.forEach(r=>{try{r();}catch{}});await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
