const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const {chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot');
(async()=>{
 const server=http.createServer((req,res)=>{const file=path.join(root,new URL(req.url,'http://test').pathname);if(!fs.existsSync(file)){res.writeHead(404);return res.end();}res.setHeader('Content-Type',file.endsWith('.js')?'text/javascript':'text/css');res.end(fs.readFileSync(file));});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});
 try{
 for(const touch of [false,true]){
 const page=await browser.newPage({viewport:{width:390,height:780},hasTouch:touch});
 await page.goto(`http://127.0.0.1:${server.address().port}/notifications.css`);
 await page.setContent('<meta name="viewport" content="width=device-width,initial-scale=1"><div class="signal-live-host" id="host"></div>');
 await page.addStyleTag({path:path.join(root,'signal-live-chart.css')});
 await page.addScriptTag({path:path.join(root,'vendor/lightweight-charts.standalone.production.js')});
 await page.evaluate(()=>{const lib=LightweightCharts;window.LightweightCharts={...lib,createChart(...args){const c=lib.createChart(...args);window.testChart=c;const add=c.addCandlestickSeries.bind(c);c.addCandlestickSeries=(...args)=>window.testSeries=add(...args);return c;}};window.fetch=async()=>({ok:true,json:async()=>({symbol:'HYPEUSDT',interval:'1',lastPrice:88.864,updatedAtUtc:'2026-10-29T12:00:00Z',levels:[{label:'ENTRY',price:80.426},{label:'TP',price:79.986},{label:'SL',price:80.843}],candles:Array.from({length:50},(_,i)=>({openTime:1793275200000+i*60000,open:88+i*.01,high:88.3+i*.01,low:87.9+i*.01,close:88.1+i*.01}))})});});
 await page.addScriptTag({path:path.join(root,'signal-live-chart.js')});
 await page.evaluate(()=>window.mounted=PhoenixLiveChart.mount(document.querySelector('#host'),'fixture'));
 await page.waitForFunction(()=>window.testSeries?.data().length===50);await page.waitForTimeout(100);
 assert.equal(await page.evaluate(()=>testChart.priceScale('right').options().mode),1,'Logarithmic scale');
 assert.ok(await page.evaluate(()=>testSeries.priceToCoordinate(80.426)>260),'Distant entry must not squash the candle range');
 const span=()=>page.evaluate(()=>Math.abs(testSeries.priceToCoordinate(88.5)-testSeries.priceToCoordinate(88)));
 await page.bringToFront();
 const before=await span();const box=await page.locator('.signal-live-canvas').boundingBox(),x=box.x+box.width-25,y=box.y+80;
 if(touch){const cdp=await page.context().newCDPSession(page);await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x,y}]});for(let d=10;d<=90;d+=10)await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x,y:y+d}]});await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});}
 else{await page.mouse.move(x,y);await page.mouse.down();await page.mouse.move(x,y+90,{steps:10});await page.mouse.up();}
 await page.waitForTimeout(100);const after=await span();assert.ok(Math.abs(after-before)>5,`Axis drag must change vertical zoom (${touch?'touch':'mouse'}): ${before} -> ${after}`);
 assert.equal(await page.evaluate(()=>testChart.priceScale('right').options().autoScale),false);
 await page.evaluate(()=>mounted.load());await page.waitForTimeout(100);assert.ok(Math.abs(await span()-after)<1,'Polling preserves vertical zoom');
 await page.locator('.signal-live-reset').click();await page.waitForTimeout(100);assert.equal(await page.evaluate(()=>testChart.priceScale('right').options().autoScale),true);
 console.log(`PASS ${touch?'touch':'mouse'} price-axis zoom, logarithmic scale, candle autoscale, polling and reset`);
 }
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
