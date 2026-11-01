const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const {chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot');
(async()=>{
const server=http.createServer((req,res)=>{const name=new URL(req.url,'http://test').pathname,file=path.resolve(root,'.'+(name==='/analysis/coins'?'/crypto-market.html':name==='/phoenix'?'/phoenix.html':name));if(!file.startsWith(root+path.sep)||!fs.existsSync(file)){res.writeHead(404).end();return;}res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.woff2':'font/woff2','.png':'image/png','.webp':'image/webp'})[path.extname(file)]||'application/octet-stream');res.end(fs.readFileSync(file));});await new Promise(r=>server.listen(0,'127.0.0.1',r));
const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});
try{for(const width of [390,1280]){
const mobile=width===390,page=await browser.newPage({viewport:{width,height:900},isMobile:mobile,hasTouch:mobile});let posted=[],failPost=true,holdCandle,oneCandle=false;const errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.route('https://**/*',route=>route.abort());
await page.route('**/vendor/lightweight-charts.standalone.production.js',async route=>{await route.fulfill({contentType:'text/javascript',body:fs.readFileSync(path.join(root,'vendor/lightweight-charts.standalone.production.js'),'utf8')+';window.LightweightCharts={...window.LightweightCharts,createChart:(()=>{const original=window.LightweightCharts.createChart;return (...args)=>{window.testChart=original(...args);return window.testChart;};})()};'});});
await page.route('**/api/**',async route=>{const url=new URL(route.request().url()),p=url.pathname;
 let data={};if(p==='/api/analysis/coins')data={assets:[{symbol:'ETHUSDT',baseSymbol:'ETH',name:'Ethereum',marketCapRank:2,activeCount:0},{symbol:'BTCUSDT',baseSymbol:'BTC',name:'Bitcoin',marketCapRank:1,activeCount:0}]};
 else if(p==='/api/analysis/learning')data={proposalSamples:0,entrySamples:0};else if(p==='/api/analysis/signal-batch')data={running:false};else if(p.includes('proposals'))data={items:[],capacity:10};
 else if(p.endsWith('/auth/me'))data={isAdmin:true};
 else if(p==='/api/manual-signals/candles'){if(url.searchParams.get('symbol')==='ETHUSDT'&&holdCandle)await holdCandle;data={symbol:url.searchParams.get('symbol'),tickSize:.01,quantityStep:.001,maximumLeverage:100,candles:Array.from({length:oneCandle?1:150},(_,i)=>({openTime:1793275200000+i*900000,open:100+Math.sin(i/5)*.5,high:100.8,low:99.2,close:100}))};}
 else if(p==='/api/signals')data=[{id:'pending-1',symbol:'BTCUSDT',direction:'Long',status:'Pending'},{id:'filled-2',symbol:'ETHUSDT',direction:'Short',status:'Filled',filledAtUtc:'2026-10-29T12:00:00Z'}];
 else if(p==='/api/manual-signals/preview'){const input=route.request().postDataJSON();data={...input};}
 else if(p==='/api/manual-signals'){const input=route.request().postDataJSON();posted.push(input);if(failPost){failPost=false;await route.abort();return;}data={...input,id:input.requestId};}
 else if(p==='/api/manual-signals/results')data={total:1,active:0,target:1,stopLoss:0,riskFree:0,expired:0,rows:[{signal:{symbol:'BTCUSDT',direction:'Long',outcome:'Target',entryPrice:100,takeProfit:102,stopLoss:98.5,expirePrice:104,positionSizeUsdt:10,leverage:10,createdAtUtc:'2026-10-29T12:00:00Z'}}],hasMore:false};
 await route.fulfill({json:data});});
await page.goto(`http://127.0.0.1:${server.address().port}/analysis/coins?embed=1`);
await page.locator('#market-tab-manual').click();
await page.locator('#manualAssets button').first().waitFor();
await page.locator('#manualChartCard').click();
await page.waitForFunction(()=>!document.getElementById('manualReview').disabled);
assert.equal(await page.locator('html').evaluate(e=>e.classList.contains('chart-workspace-open')),true);
assert.equal(await page.locator('.workspace-current>span').isVisible(),true,'Selected timeframe remains visible on mobile');
const bounds=await page.locator('#manualEditor').boundingBox();assert.equal(bounds.y,0);assert.equal(bounds.height,900);
assert.ok(await page.locator('#manualChart').evaluate(e=>e.clientHeight>700),'Chart uses available viewport');
assert.ok(await page.locator('.manual-controls').evaluate(e=>e.getBoundingClientRect().bottom<=innerHeight),'Inputs fit viewport');
await page.mouse.wheel(0,600);assert.equal(await page.evaluate(()=>scrollY),0);
await page.locator('[data-group=fib]').click();assert.equal(await page.locator('#manualToolList button:visible').count(),4);
await page.locator('[data-manual-tool=fib]').click();assert.equal(await page.locator('.workspace-palette').evaluate(e=>e.open),false);
const entryTitle=await page.locator('[data-level=entry]').getAttribute('title');
const row=await page.locator('.manual-controls>label').evaluateAll(es=>es.map(e=>e.getBoundingClientRect().top));assert.equal(new Set(row).size,1,'Checkbox and three fields share one row');
await page.locator('#manualShowLevels').uncheck();
assert.equal(await page.locator('.manual-overlay').isVisible(),false,'All signal handles hide');
assert.equal(await page.locator('#manualReview').isDisabled(),true,'Hidden levels cannot be submitted accidentally');
await page.locator('#manualShowLevels').check();
assert.equal(await page.locator('.manual-overlay').isVisible(),true);
assert.equal(await page.locator('[data-level=entry]').getAttribute('title'),entryTitle,'Toggling keeps existing prices');
assert.equal(await page.locator('#manualReview').isEnabled(),true);
const plot=await page.locator('#manualChart').boundingBox();
const move=async(x,y,dx,dy)=>{if(mobile){const cdp=await page.context().newCDPSession(page);await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x,y}]});for(let i=1;i<=8;i++)await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:x+dx*i/8,y:y+dy*i/8}]});await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});}else{await page.mouse.move(x,y);await page.mouse.down();await page.mouse.move(x+dx,y+dy,{steps:8});await page.mouse.up();}};
const range=await page.evaluate(()=>testChart.timeScale().getVisibleLogicalRange());
await move(plot.x+100,plot.y+200,70,0);
assert.notDeepEqual(await page.evaluate(()=>testChart.timeScale().getVisibleLogicalRange()),range,'Pan remains active with fib armed');
assert.equal(await page.locator('.manual-drawing [data-drawing]').count(),0,'Drag places no anchor');
const priceBefore=await page.locator('[data-level=entry]').evaluate(e=>e.style.top);
await move(plot.x+plot.width-25,plot.y+250,0,100);
assert.notEqual(await page.locator('[data-level=entry]').evaluate(e=>e.style.top),priceBefore,'Price axis scales while drawing tool armed');
const timeBefore=await page.evaluate(()=>testChart.timeScale().getVisibleLogicalRange());
await move(plot.x+100,plot.y+plot.height-12,70,0);
assert.notDeepEqual(await page.evaluate(()=>testChart.timeScale().getVisibleLogicalRange()),timeBefore,'Time axis scales while drawing tool armed');
const tap=async(x,y)=>mobile?page.touchscreen.tap(x,y):page.mouse.click(x,y);
await tap(plot.x+60,plot.y+200);await tap(plot.x+130,plot.y+260);
assert.equal(await page.locator('.manual-drawing [data-drawing]').count(),1,'Two taps complete fib');
assert.equal(await page.locator('[data-manual-tool=cursor]').evaluate(e=>e.classList.contains('active')),true,'Completion restores cursor automatically');
await tap(plot.x+180,plot.y+300);assert.equal(await page.locator('.manual-drawing [data-drawing]').count(),1,'Next tap starts no new drawing');
await page.locator('[data-group=drawing]').click();await page.locator('[data-manual-tool=channel]').click();
await tap(plot.x+50,plot.y+200);await tap(plot.x+120,plot.y+260);await tap(plot.x+90,plot.y+330);
assert.equal(await page.locator('.manual-drawing [data-drawing]').count(),2,'Three taps complete channel');
assert.equal(await page.locator('[data-manual-tool=cursor]').evaluate(e=>e.classList.contains('active')),true);
await page.locator('.workspace-interval>button').last().click();assert.equal(await page.locator('#manualInterval').inputValue(),'30');
await page.goBack();await page.locator('#manualWatchlist').waitFor({state:'visible'});
assert.equal(await page.locator('html').evaluate(e=>e.classList.contains('chart-workspace-open')),false);
assert.deepEqual(errors,[]);console.log('PASS fixed chart workspace',width);
}
}finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
