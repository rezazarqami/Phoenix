const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const base=path.join(__dirname,'../Phoenix.Web/wwwroot');const {chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium');
(async()=>{
 const server=http.createServer((req,res)=>{let target=new URL(req.url,'http://fixture').pathname;if(target==='/notifications')target='/notifications.html';const file=path.resolve(base,'.'+target);if(!file.startsWith(base+path.sep)||!fs.existsSync(file)){res.writeHead(404);res.end();return;}res.setHeader('Content-Type',({'.html':'text/html','.css':'text/css','.js':'application/javascript','.png':'image/png','.webp':'image/webp','.woff2':'font/woff2'})[path.extname(file)]||'application/octet-stream');res.end(fs.readFileSync(file));});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});const page=await browser.newPage();let chartRequests=0;
 try{
  await page.route('**/api/**',async route=>{
   const url=new URL(route.request().url());if(url.pathname.endsWith('/image')){chartRequests++;await route.fulfill({contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="600" height="300"><rect width="600" height="300" fill="white"/><path d="M10 250L100 150 200 200 400 30 590 100" stroke="#36a9a0" fill="none"/></svg>'});return;}
   if(url.pathname.endsWith('/live-chart')){await route.fulfill({json:{symbol:'XMRUSDT',interval:'5',lastPrice:541,updatedAtUtc:'2026-10-27T10:00:00Z',candles:Array.from({length:50},(_,i)=>({openTime:1793070000000+i*300000,open:539+i/100,high:542,low:537,close:540+i/100})),levels:[{label:'CEILING',price:550},{label:'FLOOR',price:530},{label:'ENTRY',price:539.3},{label:'TP',price:543.16},{label:'SL',price:535.7}]}});return;}
   if(url.pathname.endsWith('/wallet-recovery')){await route.fulfill({json:{state:'waiting',message:'بازیابی خودکار موجودی‌های ثبت‌نشده از بای‌بیت'}});return;}
   const results=url.searchParams.get('view')==='results';
   await route.fulfill({json:{events:Array.from({length:20},(_,i)=>{const kind=(results?['Target','RiskFree','StopLoss','RiskFree','RiskFree']:['Target','FirstProtection','RiskFreeReached','Opened','NearEntry'])[i%5];return {id:'00000000-0000-4000-8000-'+String(i+1).padStart(12,'0')+':'+kind,kind,sequence:30-i,at:new Date(Date.UTC(2026,9,27,3,30-i)).toISOString(),symbol:['XMRUSDT','1000PEPEUSDT','NIGHTUSDT','JTOUSDT','QNTUSDT'][i%5],direction:'Long',timeframe:'5',entryPrice:539.3,takeProfit:543.16,stopLoss:535.7,walletBalanceUsdt:i===2?null:13.15129586,exitKind:i%5===1?'FirstStepLimit':i%5===3?'SecondStepLimit':null,walletSource:i===0?'BybitTransaction':null,walletAtUtc:'2026-10-27T03:30:00Z'};}),hasMore:true,before:1,focusFound:true}});
  });
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  for(const [width,height] of [[360,640],[390,780],[412,820],[1280,800]]){
   chartRequests=0;await page.setViewportSize({width,height});await page.goto('http://127.0.0.1:'+server.address().port+'/notifications?embed=1');await page.locator('.event-card').first().waitFor();await page.evaluate(()=>document.fonts.ready);
   assert.ok(await page.locator('.journal-hero').evaluate(e=>getComputedStyle(e).backgroundImage.includes('phoenix-ice-sky.webp')),'Reuse home sky asset');assert.ok(await page.locator('.journal-brand img').evaluate(e=>e.complete&&e.naturalWidth>0),'Reuse real Phoenix logo');
   await page.locator('#journal-tab-results').click();await page.waitForFunction(()=>document.querySelectorAll('.event-wallet').length===20);
   assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'Journal must not overflow horizontally');
   const boxes=await page.locator('[role=tab]').evaluateAll(es=>es.map(e=>e.getBoundingClientRect().toJSON()));assert.ok(boxes.every(b=>b.x>=0&&b.right<=width+1),'Tabs must fit mobile');
   assert.ok(await page.locator('.event-card').evaluateAll(es=>es.every(e=>e.lastElementChild.classList.contains('event-wallet'))),'Balances must be at bottom of every result');
   assert.equal(await page.locator('.wallet-missing').count(),1,'Unknown balances must be explicit');
   assert.deepEqual(await page.locator('.event-kind').allTextContents().then(xs=>xs.slice(0,5)),['TP','RF1','SL','RF2','RF']);
   assert.equal(await page.locator('.event-levels:visible').count(),0,'Prices must be hidden until details are opened');assert.equal(chartRequests,0,'Closed cards must not fetch chart images');
   await page.locator('.event-card').first().evaluate(e=>window.scrollTo(0,e.getBoundingClientRect().top+scrollY));
   const visible=await page.locator('.event-card').evaluateAll(es=>es.filter(e=>{const r=e.getBoundingClientRect();return r.top>=-1&&r.bottom<=innerHeight;}).length);
   assert.ok(visible>=10,`At least ten collapsed results must fit in ${height}px; got ${visible}`);
   assert.ok(await page.locator('.event-card').first().evaluate(e=>e.getBoundingClientRect().height<=58),'Result rows must remain compact');
   if(process.env.JOURNAL_SCREENSHOTS)await page.screenshot({path:process.env.JOURNAL_SCREENSHOTS+`/compact-${width}.png`});
   await page.locator('.event-card .event-detail>summary').first().click();await page.locator('.event-card .signal-live-canvas canvas').first().waitFor();
   assert.equal(await page.locator('.event-levels:visible').count(),1);assert.equal(chartRequests,0,'Live chart must not load the old image');assert.ok((await page.locator('.signal-live-status').first().textContent()).includes('541'),'Current price must be shown');assert.ok(await page.locator('.event-detail[open] .event-levels').textContent().then(t=>t.includes('539.3')),'Original entry level must remain accessible');assert.ok(await page.locator('.event-detail[open] .wallet-source').isVisible(),'Balance provenance belongs in expanded details');
   if(process.env.JOURNAL_SCREENSHOTS)await page.screenshot({path:process.env.JOURNAL_SCREENSHOTS+`/expanded-${width}.png`});
   await page.locator('.signal-archive-chart>summary').first().click();await page.locator('.signal-archive-chart img').first().evaluate(img=>img.decode());assert.equal(chartRequests,1,'Original image is only requested when explicitly selected');await page.locator('.event-card .event-detail>summary').first().click();assert.equal(await page.locator('.event-levels:visible').count(),0);
   console.log(`PASS compact journal ${width}x${height}: ${visible} complete results visible, TP/SL/RF stages, lazy chart and hidden price details.`);
  }
  assert.deepEqual(errors,[]);
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
