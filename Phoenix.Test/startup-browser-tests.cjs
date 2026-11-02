const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const {chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium'),base=path.join(__dirname,'../Phoenix.Web/wwwroot');
(async()=>{
 const hits={},errors=[],check={status:'connected',ageSeconds:0};
 const server=http.createServer((req,res)=>{const u=new URL(req.url,'http://fixture'),name=u.pathname;hits[name]=(hits[name]||0)+1;
  if(name.startsWith('/api/')){
   let data={};
   if(name==='/api/auth/me')data={isAdmin:true,viewerOnly:false};
   if(name==='/api/status')data={lastPrice:100,tradingEnvironment:'Real',connection:{status:'connected',publicApi:check,account:check,monitoring:check}};
   if(name==='/api/signals/active-count')data={count:1};
   if(name==='/api/signals')data=[{id:'fixture',symbol:'BTCUSDT',direction:'Long',status:'Pending',timeframe:'15',entryPrice:100,takeProfit:110,stopLoss:90}];
   if(name==='/api/signals/timeframe-rules')data=[];
   if(name.includes('/live-chart'))data={symbol:'BTCUSDT',lastPrice:101,interval:'15',updatedAtUtc:new Date().toISOString(),candles:[{openTime:1793606400000,open:100,high:102,low:99,close:101}],levels:[]};
   if(name==='/api/instruments')data={symbols:['BTCUSDT']};
   if(name.endsWith('/limits'))data={maximumLeverage:100};
   if(name==='/api/market/overview')data={closes:[100,101,102]};
   if(name==='/api/market/commodities')data={items:['XAU','BRENT','USD_TOMAN','GOLD18_TOMAN','EUR_TOMAN'].map(symbol=>({symbol,price:100,atUtc:new Date().toISOString(),source:'fixture',currency:symbol.includes('TOMAN')?'TOMAN':'USD'}))};
   if(name==='/api/market-risk')data={alerts:[{id:'alert',severity:'danger',at:new Date().toISOString(),title:'هشدار آزمایشی',detail:'fixture'}]};
   res.setHeader('Content-Type','application/json');return res.end(JSON.stringify(data));
  }
  let file=path.resolve(base,'.'+(name==='/phoenix'?'/phoenix.html':name==='/login'?'/login.html':name));if(!file.startsWith(base+path.sep)||!fs.existsSync(file)){res.writeHead(404).end();return;}
  if(process.env.PHOENIX_AUDIT_BASE){const old=path.join(process.env.PHOENIX_AUDIT_BASE,path.relative(base,file));if(fs.existsSync(old))file=old;}
  res.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css','.jpg':'image/jpeg','.png':'image/png','.woff2':'font/woff2','.webp':'image/webp'})[path.extname(file)]||'application/octet-stream');
  if(u.searchParams.has('v')&&path.extname(file)!=='.html')res.setHeader('Cache-Control','private,max-age=3600');res.end(fs.readFileSync(file));
 });await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});
 try{
  const page=await browser.newPage({viewport:{width:390,height:844}});page.on('pageerror',e=>errors.push(e.message));await page.goto('http://127.0.0.1:'+server.address().port+'/phoenix');await page.waitForFunction(()=>document.getElementById('goldPrice').textContent==='100.00');
  if(process.env.MEASURE_STARTUP){await page.waitForLoadState('networkidle');console.log(JSON.stringify({requests:hits,errors}));return;}
  assert.equal(hits['/api/auth/me'],1,'Four startup modules share one session read');assert.equal(hits['/api/signals']||0,0);assert.equal(hits['/api/instruments']||0,0);assert.equal(hits['/vendor/lightweight-charts.standalone.production.js']||0,0);
  assert.equal(await page.locator('#riskToast').isVisible(),true,'Global risk warnings remain active on home');await page.locator('#riskToastClose').click();
  await page.evaluate(()=>PhoenixNavigate('crypto',true,'section=manual&mode=form'));await page.waitForFunction(()=>document.getElementById('maximumLeverage').textContent.includes('۱۰۰'));assert.equal(hits['/api/instruments'],1);
  await page.evaluate(()=>{PhoenixNavigate('home');PhoenixNavigate('crypto',true,'section=manual&mode=form');});assert.equal(hits['/api/instruments'],1);
  await page.evaluate(()=>PhoenixNavigate('tracking'));await page.locator('.active-expand').waitFor();await page.locator('.active-expand').click();await page.waitForFunction(()=>document.querySelector('.signal-live-host canvas'));
  await page.waitForFunction(()=>document.querySelector('.signal-live-status')?.dataset.stale==='false');
  assert.equal(hits['/vendor/lightweight-charts.standalone.production.js'],1,'Expanding a real signal loads and renders the deferred chart library');
  await page.locator('.active-expand').click();await page.locator('.active-expand').click();await page.waitForFunction(()=>document.querySelector('.signal-live-host canvas'));assert.equal(hits['/vendor/lightweight-charts.standalone.production.js'],1);
  const overviews=hits['/api/market/overview'];await page.evaluate(()=>window.dispatchEvent(new Event('visibilitychange')));assert.equal(hits['/api/market/overview'],overviews,'Hidden home does not request chart history');
  const origin='http://127.0.0.1:'+server.address().port;await page.goto(origin+'/login');await page.waitForLoadState('networkidle');const logo=hits['/analysis-assets/phoenix-ice-logo.png'];await page.goto(origin+'/login?again=1');await page.waitForLoadState('networkidle');assert.equal(hits['/analysis-assets/phoenix-ice-logo.png'],logo,'Unchanged login artwork is reused without another network request');
  assert.deepEqual(errors,[]);console.log('PASS startup browser: one identity request, deferred catalog and chart library, working live chart, risk warnings, visible-only overview and reused login artwork.');
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
