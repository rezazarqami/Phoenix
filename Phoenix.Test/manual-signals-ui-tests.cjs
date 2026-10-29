const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const {chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot');
(async()=>{
const server=http.createServer((req,res)=>{const name=new URL(req.url,'http://test').pathname,file=path.resolve(root,'.'+(name==='/analysis/coins'?'/crypto-market.html':name));if(!file.startsWith(root+path.sep)||!fs.existsSync(file)){res.writeHead(404).end();return;}res.setHeader('Content-Type',({'.html':'text/html','.js':'text/javascript','.css':'text/css','.woff2':'font/woff2','.png':'image/png','.webp':'image/webp'})[path.extname(file)]||'application/octet-stream');res.end(fs.readFileSync(file));});await new Promise(r=>server.listen(0,'127.0.0.1',r));
const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});
try{for(const width of [390,1280]){
const mobile=width===390,page=await browser.newPage({viewport:{width,height:900},isMobile:mobile,hasTouch:mobile});let posted=[],failPost=true,holdCandle;const errors=[];page.on('pageerror',e=>errors.push(e.message));
await page.route('**/api/**',async route=>{const url=new URL(route.request().url()),p=url.pathname;
 let data={};if(p==='/api/analysis/coins')data={assets:[{symbol:'ETHUSDT',baseSymbol:'ETH',name:'Ethereum',marketCapRank:2,activeCount:0},{symbol:'BTCUSDT',baseSymbol:'BTC',name:'Bitcoin',marketCapRank:1,activeCount:0}]};
 else if(p==='/api/analysis/learning')data={proposalSamples:0,entrySamples:0};else if(p==='/api/analysis/signal-batch')data={running:false};else if(p.includes('proposals'))data={items:[],capacity:10};
 else if(p.endsWith('/auth/me'))data={isAdmin:true};
 else if(p==='/api/manual-signals/candles'){if(url.searchParams.get('symbol')==='ETHUSDT'&&holdCandle)await holdCandle;data={symbol:url.searchParams.get('symbol'),tickSize:.01,quantityStep:.001,maximumLeverage:100,candles:Array.from({length:150},(_,i)=>({openTime:1793275200000+i*900000,open:100+Math.sin(i/5)*.5,high:100.8,low:99.2,close:100}))};}
 else if(p==='/api/manual-signals/preview'){const input=route.request().postDataJSON();data={...input};}
 else if(p==='/api/manual-signals'){const input=route.request().postDataJSON();posted.push(input);if(failPost){failPost=false;await route.abort();return;}data={...input,id:input.requestId};}
 else if(p==='/api/manual-signals/results')data={total:1,active:0,target:1,stopLoss:0,riskFree:0,expired:0,rows:[{signal:{symbol:'BTCUSDT',direction:'Long',outcome:'Target',entryPrice:100,takeProfit:102,stopLoss:98.5,expirePrice:104,positionSizeUsdt:10,leverage:10,createdAtUtc:'2026-10-29T12:00:00Z'}}],hasMore:false};
 await route.fulfill({json:data});});
await page.goto(`http://127.0.0.1:${server.address().port}/analysis/coins?embed=1`);await page.locator('#market-tab-manual').click();await page.locator('.manual-level-handle').first().waitFor();await page.waitForFunction(()=>!document.getElementById('manualReview').disabled);await page.bringToFront();
assert.equal(await page.locator('#manualAssets button').first().getAttribute('data-symbol'),'BTCUSDT','Use market-cap rank');assert.equal(await page.locator('#manualSymbol').textContent(),'BTCUSDT');
assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),'No horizontal overflow');
assert.equal(await page.locator('#manualEditor input[type=number]').count(),2,'Only margin and leverage fields, no manual price form');
let text=await page.locator('[data-level=target] span').textContent();assert.match(text,/\+2\.00/,'10 margin x10, 2% target = $2');assert.match(await page.locator('[data-level=stop] span').textContent(),/1\.50/);assert.doesNotMatch(await page.locator('[data-level=expire] span').textContent(),/\$/);
await page.locator('#manualMargin').fill('20');assert.match(await page.locator('[data-level=target] span').textContent(),/\+4\.00/,'PnL follows margin immediately');
const target=page.locator('[data-level=target]');await target.scrollIntoViewIfNeeded();const box=await target.boundingBox(),before=await target.locator('span').textContent();
if(mobile){const cdp=await page.context().newCDPSession(page);const x=box.x+box.width-30,y=box.y+box.height/2;await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x,y}]});for(let d=1;d<=5;d++)await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x,y:y-d*4}]});await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});}
else{await page.mouse.move(box.x+box.width-30,box.y+box.height/2);await page.mouse.down();await page.mouse.move(box.x+box.width-30,box.y+box.height/2-20,{steps:5});await page.mouse.up();}
assert.notEqual(await target.locator('span').textContent(),before,'Dragging changes projected profit');
await page.locator('[data-level=entry]').focus();await page.keyboard.press('ArrowUp');assert.ok(await page.locator('[data-level=entry]').getAttribute('title').then(t=>t.includes('100.01')),'Keyboard moves entry by tick');
await page.locator('[data-manual-tool=trend]').click();const chartBox=await page.locator('#manualChart').boundingBox();if(mobile){await page.touchscreen.tap(chartBox.x+30,chartBox.y+120);await page.touchscreen.tap(chartBox.x+70,chartBox.y+170);}else{await page.mouse.click(chartBox.x+30,chartBox.y+120);await page.mouse.click(chartBox.x+70,chartBox.y+170);}assert.equal(await page.locator('.manual-drawing line').count(),1,'Trend tool places two anchors');await page.locator('#manualUndo').click();assert.equal(await page.locator('.manual-drawing line').count(),0);await page.locator('[data-manual-tool=cursor]').click();
await page.locator('#manualDirection').selectOption('Short');assert.match(await target.locator('span').textContent(),/\+/,'Short target keeps positive PnL');
await page.locator('#manualReview').click();await page.locator('#manualConfirm').waitFor({state:'visible'});assert.equal(posted.length,0,'Review does not enqueue');
await page.locator('#manualSubmit').click();await page.waitForFunction(()=>document.getElementById('manualConfirmStatus').textContent.includes('دوباره'));assert.equal(posted.length,1);
await page.locator('#manualSubmit').click();await page.waitForFunction(()=>!document.getElementById('manualConfirm').open);assert.equal(posted.length,2);assert.deepEqual(posted[0],posted[1],'Uncertain retry uses same ID and same frozen levels');assert.equal(posted[1].direction,'Short');assert.equal(posted[1].positionSizeUsdt,20);
await page.locator('#manualResultsTab').click();await page.locator('#manualResultRows tr').waitFor();assert.match(await page.locator('#manualTotals').textContent(),/تارگت/);assert.match(await page.locator('#manualResultRows').textContent(),/BTCUSDT/);
await page.locator('#manualEditorTab').click();await page.locator('#manualAssets button[data-symbol=ETHUSDT]').click();await page.waitForFunction(()=>document.getElementById('manualSymbol').textContent==='ETHUSDT'&&!document.getElementById('manualReview').disabled);
if(process.env.MANUAL_SCREENSHOTS)await page.screenshot({path:process.env.MANUAL_SCREENSHOTS+`/manual-${width}.png`,fullPage:true});
assert.deepEqual(errors,[],'No browser runtime errors');console.log(`PASS manual ${width}: ranked assets, live ${mobile?'touch':'mouse'} line PnL, keyboard, drawing tools, short direction, preview, stable retry and separate results.`);
}
}finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
