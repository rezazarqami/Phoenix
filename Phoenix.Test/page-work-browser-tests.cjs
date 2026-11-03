const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const {chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium'),base=path.join(__dirname,'../Phoenix.Web/wwwroot');
(async()=>{
 const hits={},posts=[],errors=[],id='11111111-1111-1111-1111-111111111111',candles=Array.from({length:40},(_,i)=>({openTime:1793664000000+i*60000,open:100,high:102,low:99,close:101}));
 const event={id:id+':Target',sequence:10,kind:'Target',symbol:'BTCUSDT',direction:'Long',at:'2026-11-03T00:00:00Z'};let events=[event];
 const server=http.createServer((req,res)=>{const u=new URL(req.url,'http://fixture'),n=u.pathname;hits[n]=(hits[n]||0)+1;if(req.method==='POST')posts.push(n);
  if(n.startsWith('/api/')){let data={};
   if(n==='/api/auth/me')data={username:'owner',isAdmin:true,viewerOnly:false};if(n==='/api/status')data={lastPrice:101,tradingEnvironment:'Real',connection:{status:'unknown'}};
   if(n==='/api/market/commodities')data={items:[]};if(n==='/api/market/overview')data={closes:[100,101]};if(n==='/api/market-risk')data={alerts:[]};if(n==='/api/signals/active-count')data={count:1};
   if(n==='/api/signals')data=[{id,symbol:'BTCUSDT',direction:'Long',status:'Pending',timeframe:'15',entryPrice:100,takeProfit:110,stopLoss:90}];if(n==='/api/signals/timeframe-rules')data=[];
   if(n.includes('/live-chart'))data={symbol:'BTCUSDT',interval:'15',lastPrice:101,updatedAtUtc:new Date().toISOString(),candles,levels:[]};if(n==='/api/manual-signals/candles')data={candles,tickSize:.01,quantityStep:.001,maximumLeverage:100};
   if(n==='/api/analysis/signal-batch')data={running:false,message:'صفی فعال نیست.'};if(n==='/api/analysis/proposals')data=[];if(n==='/api/analysis/learning')data={proposalSamples:10,entrySamples:20};
   if(n==='/api/analysis/shadow-signals')data={summary:{total:0,waitingEntry:0,entered:0,target:0,stopLoss:0},signals:[]};if(n==='/api/analysis/coins')data={assets:[{symbol:'BTCUSDT',baseSymbol:'BTC',name:'Bitcoin',marketCapRank:1,activeCount:1}]};
   if(n==='/api/mobile/history')data={events,before:1,hasMore:false,focusFound:true};if(n.includes('/wallet-recovery'))data={state:'waiting'};
   res.setHeader('Content-Type','application/json');const send=()=>res.end(JSON.stringify(data));return n==='/api/analysis/coins'?setTimeout(send,200):send();
  }
  const aliases={'/phoenix':'/phoenix.html','/analysis/coins':'/crypto-market.html','/notifications':'/notifications.html'},file=path.resolve(base,'.'+(aliases[n]||n));
  if(!file.startsWith(base+path.sep)||!fs.existsSync(file)){res.writeHead(404).end();return;}res.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css','.jpg':'image/jpeg','.png':'image/png','.webp':'image/webp','.woff2':'font/woff2'})[path.extname(file)]||'application/octet-stream');res.end(fs.readFileSync(file));
 });await new Promise(r=>server.listen(0,'127.0.0.1',r));const origin='http://127.0.0.1:'+server.address().port;
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});
 try{
  const page=await browser.newPage({viewport:{width:390,height:844}});page.on('pageerror',e=>errors.push(e.message));await page.goto(origin+'/phoenix#crypto');const child=page.frameLocator('#cryptoFrameHost iframe');await child.locator('#batchStatus').filter({hasText:'صفی فعال نیست.'}).waitFor();
  assert.equal(hits['/api/analysis/coins']||0,0);assert.equal(hits['/api/analysis/learning']||0,0);assert.equal(hits['/api/analysis/proposals']||0,0);
  await page.evaluate(()=>PhoenixNavigate('home'));const batch=hits['/api/analysis/signal-batch'];await page.waitForTimeout(10500);assert.equal(hits['/api/analysis/signal-batch'],batch);
  await page.evaluate(()=>PhoenixNavigate('crypto',true,'section=manual'));await child.locator('#manualSymbol').filter({hasText:'BTCUSDT'}).waitFor({state:'attached'});
  await child.locator('#manualSymbol').evaluate(()=>window.PhoenixMarketNavigate('search'));await child.locator('.market-row').waitFor();assert.equal(hits['/api/analysis/coins'],1);
  await child.locator('#marketRows').evaluate(()=>window.PhoenixMarketNavigate('manual'));await child.locator('#manualAssets button').first().click();if(await child.locator('#manualShowLevels').count())await child.locator('#manualShowLevels').check();await child.locator('[data-level=target]').waitFor();assert.match(await child.locator('[data-level=target] span').textContent(),/2\.00/);await child.locator('#manualMargin').fill('20');assert.match(await child.locator('[data-level=target] span').textContent(),/4\.00/);await child.locator('#manualMargin').fill('10');
  await child.locator('#marketRows').evaluate(()=>window.PhoenixMarketNavigate('approved'));await child.locator('#shadowSummary').filter({hasText:'منتظر ورود'}).waitFor();await page.waitForTimeout(200);assert.equal(hits['/api/analysis/shadow-signals'],1);
  await child.locator('#shadowRows').evaluate(()=>window.PhoenixMarketNavigate('review'));await child.locator('#reviewStatus').filter({hasText:'پیشنهاد آماده'}).waitFor();assert.ok(hits['/api/analysis/proposals']>=1);
  await page.evaluate(()=>PhoenixNavigate('notifications'));const journal=page.frameLocator('#journalFrameHost iframe');await journal.locator('.event-card').waitFor();assert.equal(await journal.locator('body').evaluate(()=>typeof LightweightCharts),'undefined');
  await journal.locator('body').evaluate(()=>{window.mounts=0;const mount=PhoenixLiveChart.mount;PhoenixLiveChart.mount=(...args)=>{window.mounts++;return mount(...args);};});
  await journal.locator('.event-detail>summary').first().click();await journal.locator('.signal-live-status[data-stale=false]').waitFor();events=[{...event,id:'22222222-2222-2222-2222-222222222222:StopLoss',sequence:11,kind:'StopLoss'},event];await journal.locator('#refreshEvents').click();await journal.locator('.event-card[data-kind=StopLoss]').waitFor();assert.equal(await journal.locator('body').evaluate(()=>window.mounts),1);
  await page.evaluate(()=>PhoenixNavigate('home'));await page.waitForTimeout(250);const charts=hits['/api/signals/'+id+'/live-chart'],history=hits['/api/mobile/history'];await page.waitForTimeout(5500);assert.equal(hits['/api/signals/'+id+'/live-chart'],charts);assert.equal(hits['/api/mobile/history'],history);
  await page.evaluate(()=>PhoenixNavigate('tracking'));await page.locator('#orders .active-expand').click();await page.locator('#orders .signal-live-status[data-stale=false]').waitFor();
  await page.evaluate(()=>{window.rowMutations=0;new MutationObserver(r=>window.rowMutations+=r.length).observe(document.querySelector('#orders .active-signal-main'),{subtree:true,childList:true,attributes:true,characterData:true});window.savedRiskButton=document.querySelector('.timeframe-risk-tabs button');});await page.waitForTimeout(5500);
  assert.equal(await page.evaluate(()=>window.rowMutations),0);assert.equal(await page.evaluate(()=>window.savedRiskButton.isConnected),true);assert.deepEqual(posts,[]);assert.deepEqual(errors,[]);
  console.log('PASS three-page browser: deferred reads, one shared market request, one shadow fetch, immediate review, retained charts, hidden-frame suspension, stable management rows and no trading POSTs.');
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
