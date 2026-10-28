const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const base=path.join(__dirname,'../Phoenix.Web/wwwroot'),{chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium');
(async()=>{
 const server=http.createServer((req,res)=>{let name=new URL(req.url,'http://fixture').pathname;name=({'/phoenix':'/phoenix.html','/analysis/coins':'/crypto-market.html'})[name]||name;const file=path.resolve(base,'.'+name);if(!file.startsWith(base+path.sep)||!fs.existsSync(file)){res.writeHead(404).end();return;}res.setHeader('Content-Type',({'.html':'text/html','.js':'application/javascript','.css':'text/css','.png':'image/png','.webp':'image/webp','.woff2':'font/woff2'})[path.extname(file)]||'application/octet-stream');res.end(fs.readFileSync(file));});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});
 const page=await browser.newPage({viewport:{width:390,height:780},isMobile:true,hasTouch:true});
 const scripts=new Set(['home-tabs.js','phoenix-ice.js','crypto-ice.js']);const origin='http://127.0.0.1:'+server.address().port;let frames=0;
 await page.route('**/*',async route=>{const u=new URL(route.request().url());if(u.hostname!=='127.0.0.1'){await route.abort();return;}if(u.pathname.startsWith('/api/')){await route.fulfill({json:{}});return;}if(u.pathname.endsWith('.js')&&!scripts.has(path.basename(u.pathname))){await route.fulfill({body:''});return;}if(u.pathname==='/analysis/coins')frames++;await route.continue();});
 try{
  await page.goto(origin+'/phoenix');await page.locator('#tab-crypto').click();const frame=page.frameLocator('#panel-crypto iframe');await frame.locator('#marketBack').waitFor();await page.evaluate(()=>document.fonts.ready);await frame.locator('body').evaluate(()=>document.fonts.ready);
  await page.waitForFunction(()=>{const f=document.querySelector('#panel-crypto iframe');return f.offsetHeight>f.contentDocument.body.scrollHeight;});
  assert.equal(await frame.locator('html').evaluate(e=>getComputedStyle(e).overflowY),'clip');
  const color=await frame.locator('.batch-box').evaluate(e=>getComputedStyle(e).backgroundImage);assert.ok(color.includes('2, 14, 36'),'Create panel uses the navy glass theme');
  await frame.locator('#batchCount').fill('37');
  const cdp=await page.context().newCDPSession(page);
  async function swipe(from,to){await cdp.send('Input.dispatchTouchEvent',{type:'touchStart',touchPoints:[{x:190,y:from}]});for(let i=1;i<=8;i++){await cdp.send('Input.dispatchTouchEvent',{type:'touchMove',touchPoints:[{x:190,y:from+(to-from)*i/8}]});await page.waitForTimeout(20);}await cdp.send('Input.dispatchTouchEvent',{type:'touchEnd',touchPoints:[]});await page.waitForTimeout(350);}
  await page.evaluate(()=>window.scrollTo({top:300,behavior:'instant'}));const start=await page.evaluate(()=>scrollY);await swipe(640,340);const down=await page.evaluate(()=>scrollY);assert.ok(down>start+80,'First upward gesture scrolls the parent immediately');
  await swipe(300,620);const up=await page.evaluate(()=>scrollY);assert.ok(up<down-80,'First reversed gesture scrolls back immediately');assert.equal(await frame.locator('html').evaluate(()=>scrollY),0,'No competing frame scroll');
  await frame.locator('#marketBack').click();await page.waitForFunction(()=>!document.getElementById('panel-home').hidden);assert.equal(await page.evaluate(()=>scrollY),0);await page.locator('#tab-crypto').click();assert.equal(await frame.locator('#batchCount').inputValue(),'37','Back navigation keeps the market draft');assert.equal(frames,1,'Back and reopen must reuse the frame');
  if(process.env.MARKET_SCREENSHOTS)await page.screenshot({path:process.env.MARKET_SCREENSHOTS+'/market-neon.png',fullPage:true});
  console.log('PASS market navy glass, themed back navigation, retained iframe/input and first touch scroll in both directions.');
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
