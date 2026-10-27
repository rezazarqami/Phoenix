const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const base=path.join(__dirname,'../Phoenix.Web/wwwroot');const {chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium');
(async()=>{
 const server=http.createServer((req,res)=>{let target=new URL(req.url,'http://fixture').pathname;if(target==='/notifications')target='/notifications.html';const file=path.resolve(base,'.'+target);if(!file.startsWith(base+path.sep)||!fs.existsSync(file)){res.writeHead(404);res.end();return;}res.setHeader('Content-Type',({'.html':'text/html','.css':'text/css','.js':'application/javascript','.png':'image/png','.webp':'image/webp','.woff2':'font/woff2'})[path.extname(file)]||'application/octet-stream');res.end(fs.readFileSync(file));});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});const page=await browser.newPage();
 try{
  await page.route('**/api/mobile/history?*',async route=>{const results=new URL(route.request().url()).searchParams.get('view')==='results';await route.fulfill({json:{events:(results?['Target','RiskFree','StopLoss']:['Target','RiskFreeReached','Opened']).map((kind,i)=>({id:i+':'+kind,kind,sequence:3-i,at:'2026-10-27T0'+(3-i)+':00:00Z',symbol:'JTOUSDT',direction:'Long',timeframe:'1',entryPrice:0.4243,takeProfit:0.4278,stopLoss:0.421,walletBalanceUsdt:i===2?null:30.28245832,exitKind:'SecondStepLimit'})),hasMore:true,before:1,focusFound:true}});});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  for(const [width,height] of [[360,640],[390,780],[412,820],[1280,800]]){
   await page.setViewportSize({width,height});await page.goto('http://127.0.0.1:'+server.address().port+'/notifications');await page.locator('.event-card').first().waitFor();await page.evaluate(()=>document.fonts.ready);
   assert.ok(await page.locator('.journal-hero').evaluate(e=>getComputedStyle(e).backgroundImage.includes('phoenix-ice-sky.webp')),'Reuse home sky asset');assert.ok(await page.locator('.journal-brand img').evaluate(e=>e.complete&&e.naturalWidth>0),'Reuse real Phoenix logo');
   await page.locator('#journal-tab-results').click();await page.waitForFunction(()=>document.querySelectorAll('.event-wallet').length===3);
   assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'Journal must not overflow horizontally');
   const boxes=await page.locator('[role=tab]').evaluateAll(es=>es.map(e=>e.getBoundingClientRect().toJSON()));assert.ok(boxes.every(b=>b.x>=0&&b.right<=width+1),'Tabs must fit mobile');
   assert.ok(await page.locator('.event-card').evaluateAll(es=>es.every(e=>e.lastElementChild.classList.contains('event-wallet'))),'Balances must be at bottom of every result');
   assert.equal(await page.locator('.wallet-missing').count(),1,'Unknown balances must be explicit');
   if(process.env.JOURNAL_SCREENSHOTS)await page.screenshot({path:process.env.JOURNAL_SCREENSHOTS+`/journal-${width}.png`,fullPage:true});
   console.log(`PASS journal ${width}x${height}: branded hero, result tabs, bottom wallet balances and no overflow.`);
  }
  assert.deepEqual(errors,[]);
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
