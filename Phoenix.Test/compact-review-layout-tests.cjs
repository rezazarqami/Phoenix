const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http');
const base=path.join(__dirname,'../Phoenix.Web/wwwroot');
const requireTest=name=>require('./ui/node_modules/'+name);
const {chromium}=requireTest('playwright-core'),lambda=requireTest('@sparticuz/chromium');
const caption=`🔎 پیشنهاد جدید Phoenix
نماد: MUSDT
جهت: Short
تایم‌فریم سیگنال: ۱ دقیقه
فاصله تا ورود: 3.4٪
فاصله ورود تا تارگت: 0.73٪
لوریج محاسبه‌شده: ×68.43
حداکثر لوریج ارز: ×20
لوریج قابل استفاده با این سقف: ×20 — محدود به سقف ارز
سقف: 1.0099
کف: 0.948
ورود: 0.9854
تارگت: 0.9782
استاپ: 0.9923
ورودی: 5 USDT
نزدیکِ هم‌تایم‌فریم (از ۱۲ نمونه): 0 تارگت / 0 استاپ / 0 ریسک‌فری
نزدیکِ سایر تایم‌فریم‌ها (از ۱۴۵ نمونه): 9 تارگت / 3 استاپ / 2 ریسک‌فری`;
const chart='<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="650" viewBox="0 0 1000 650"><rect width="1000" height="650" fill="white"/>'+Array.from({length:90},(_,i)=>{const y=280+100*Math.sin(i/8),up=i%3;return `<path d="M${i*10+30} ${y-45}v85" stroke="${up?'#33a996':'#db6376'}"/><rect x="${i*10+27}" y="${y-20}" width="6" height="35" fill="${up?'#33a996':'#db6376'}"/>`;}).join('')+'<path d="M20 160H970M20 380H970" stroke="#c5b65a"/><path d="M20 300H970M20 330H970" stroke="#50a9b8"/></svg>';
const scripts=new Set(['app.js','live-positions.js','home-tabs.js','phoenix-ice.js','crypto-market.js','crypto-ice.js','signal-batch-controls.js','in-app-review.js','crypto-statistics.js']);
(async()=>{
 const server=http.createServer((req,res)=>{
  let target=new URL(req.url,'http://fixture').pathname;if(target==='/phoenix')target='/phoenix.html';if(target==='/analysis/coins')target='/crypto-market.html';
  if(target==='/fixture-chart.svg'){res.setHeader('Content-Type','image/svg+xml');res.end(chart);return;}
  const file=path.resolve(base,'.'+target);if(!file.startsWith(base+path.sep)||!fs.existsSync(file)){res.writeHead(404);res.end();return;}
  res.setHeader('Content-Type',({'.html':'text/html','.css':'text/css','.js':'application/javascript','.svg':'image/svg+xml','.woff2':'font/woff2','.png':'image/png','.webp':'image/webp','.jpg':'image/jpeg'})[path.extname(file)]||'application/octet-stream');res.end(fs.readFileSync(file));
 });await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});
 const page=await browser.newPage();
 try{
  for(const [width,height] of [[360,640],[390,780],[412,820],[1280,800]]){
   await page.setViewportSize({width,height});await page.unroute('**/*');page.removeAllListeners('pageerror');let proposals=[],imageHold=null,decisionHold=null;const errors=[];page.on('pageerror',e=>errors.push(e.message));
   await page.route('**/*',async route=>{
    const url=new URL(route.request().url());if(url.pathname==='/fixture-chart.svg'&&imageHold)await imageHold;if(url.pathname.includes('/decision')&&decisionHold)await decisionHold;
    if(url.pathname.startsWith('/api/')){
     let data={};if(url.pathname==='/api/auth/me')data={isAdmin:true};else if(url.pathname==='/api/signals'||url.pathname==='/api/history')data=[];else if(url.pathname==='/api/crypto-statistics')data={updatedAtUtc:'2026-10-30T01:00:00Z',items:[{symbol:'BTCUSDT',total:8,open:1,targets:2,stops:2,riskFree:3,otherClosed:0},{symbol:'ETHUSDT',total:5,open:0,targets:1,stops:2,riskFree:2,otherClosed:0}]};else if(url.pathname==='/api/positions/live')data={updatedAtUtc:'2026-10-27T01:00:00Z',items:[{position:{symbol:'BTCUSDT',side:'Buy',size:0.015,leverage:20,averagePrice:79826.7,markPrice:80000,unrealisedPnl:2.5995,initialMargin:59.87,returnPercent:4.34,takeProfit:82000,stopLoss:79500,liquidationPrice:76000},signalId:'fixture',firstProtection:true,timeframe:'15'},{position:{symbol:'ETHUSDT',side:'Sell',size:0.5,leverage:10,averagePrice:3000,markPrice:3010,unrealisedPnl:-5,initialMargin:150,returnPercent:-3.33},signalId:'fixture2',riskFreeReached:true,timeframe:'60'}]};else if(url.pathname==='/api/analysis/proposals')data=proposals;else if(url.pathname.includes('signal-batch'))data={running:false,message:'آماده'};else if(url.pathname.includes('market/overview'))data={closes:[100,102,99,104]};else if(url.pathname.includes('coins'))data={assets:[]};else if(url.pathname.includes('shadow'))data={summary:{total:0},signals:[]};else if(url.pathname.includes('learning'))data={entrySamples:100,proposalSamples:50};
     await route.fulfill({json:data});return;
    }
    if(url.pathname.endsWith('.js')&&!scripts.has(path.basename(url.pathname))){await route.fulfill({body:''});return;}
    if(url.hostname!=='127.0.0.1'){await route.abort();return;}await route.continue();
   });
   await page.goto('http://127.0.0.1:'+server.address().port+'/phoenix');await page.evaluate(()=>document.fonts.ready);
   await page.locator('#connection').click();await page.locator('#connectionDetails').waitFor({state:'visible'});
   const connectionBox=await page.locator('#connectionDetails').boundingBox();
   assert.ok(connectionBox.x>=0 && connectionBox.x+connectionBox.width<=width+1 && connectionBox.height<=height*.81,'Connection details must fit mobile and desktop');
   assert.match(await page.locator('#connectionDetailsBody').innerText(),/خواندن حساب Bybit/);
   await page.locator('#closeConnectionDetails').click();await page.locator('#notificationToggle').click();
   const title=await page.locator('#notificationMenu>b').boundingBox(),link=await page.locator('.notification-history-link').boundingBox();
   assert.ok(link.y>=title.y+title.height,'History link must be below the title');assert.ok(link.x>=0&&link.x+link.width<=width+1,'Popover link must stay in the viewport');
   if(process.env.REVIEW_SCREENSHOTS)await page.screenshot({path:process.env.REVIEW_SCREENSHOTS+`/bell-${width}.png`});
   await page.locator('#notificationToggle').click();
   await page.locator('#signal-tab-positions').click();await page.locator('.position-card').first().waitFor();
   assert.equal(await page.locator('.position-card').count(),2);
   assert.equal(await page.locator('.position-pnl.positive').count(),1);assert.equal(await page.locator('.position-pnl.negative').count(),1);
   assert.ok(await page.locator('#signal-pane-positions').evaluate(e=>e.scrollWidth<=e.clientWidth+1),'Live position cards must not overflow horizontally');
   const tabBoxes=await page.locator('[data-signal-tab]').evaluateAll(es=>es.map(e=>e.getBoundingClientRect().toJSON()));assert.ok(tabBoxes.every(b=>b.x>=0&&b.right<=width+1),'Four tabs must fit the viewport');
   if(process.env.REVIEW_SCREENSHOTS)await page.locator('.signal-center').screenshot({path:process.env.REVIEW_SCREENSHOTS+`/positions-${width}.png`});
   await page.locator('#tab-statistics').click();await page.locator('#statisticsRiskFree').filter({hasText:'۵'}).waitFor();
   assert.equal(await page.locator('#statisticsTotal').textContent(),'۱۳','Risk-free total must sum all symbols');
   const stats=await page.locator('#panel-statistics').evaluate(e=>({overflow:e.scrollWidth>e.clientWidth+1,cards:[...e.querySelectorAll('.statistics-summary>span')].map(x=>({h:x.getBoundingClientRect().height,color:getComputedStyle(x).backgroundColor})),title:getComputedStyle(e.querySelector('h2')).color}));
   assert.equal(stats.overflow,false,'Statistics fit mobile width');assert.equal(stats.cards.length,5);assert.ok(stats.cards.every(x=>x.h<80&&x.color==='rgb(7, 26, 50)'),'Statistics cards must be compact and match the navy theme');assert.equal(stats.title,'rgb(227, 239, 255)');
   if(process.env.REVIEW_SCREENSHOTS)await page.locator('#panel-statistics').screenshot({path:process.env.REVIEW_SCREENSHOTS+`/statistics-${width}.png`});
   await page.locator('#tab-crypto').click();const frame=page.frameLocator('#panel-crypto iframe');await frame.locator('#market-tab-review').click();
   assert.equal(await frame.locator('.review-heading').evaluate(e=>getComputedStyle(e).backgroundColor),'rgba(0, 0, 0, 0)','Review heading must not inherit the dark global header');
   const heading=await frame.locator('.review-heading').evaluate(e=>{const rect=x=>x.getBoundingClientRect().toJSON();return {box:rect(e),title:rect(e.querySelector('h2')),copy:rect(e.querySelector('p')),clear:rect(e.querySelector('#clearReviewDesk')),restore:rect(e.querySelector('#restoreReviewDesk'))};});
   assert.ok(heading.title.height<40,'Review title must not become vertical');
   if(width<=700){assert.ok(heading.title.width>width*.65,'Mobile title needs its own full row');assert.ok(heading.clear.top>=heading.copy.bottom,'Actions belong below the title and description');assert.ok(Math.abs(heading.clear.top-heading.restore.top)<2,'Desk actions share a compact row');assert.ok(heading.box.height<170,'Mobile review heading must stay compact');assert.ok(heading.clear.height<=32&&heading.restore.height<=32,'Desk action buttons must remain small');assert.ok(heading.clear.width<140&&heading.restore.width<140,'Desk action buttons use content width');}
   if(process.env.REVIEW_SCREENSHOTS)await page.screenshot({path:process.env.REVIEW_SCREENSHOTS+`/empty-${width}.png`,fullPage:true});
   let releaseImage;imageHold=new Promise(r=>releaseImage=r);
   proposals=[{key:'fixture',symbol:'MUSDT',caption,image:'/fixture-chart.svg',createdAt:'2026-01-01T00:00:00Z',state:'Ready'}];
   await frame.locator('#market-tab-review').click();await frame.locator('#reviewDetail .review-actions').waitFor();
   assert.equal(await page.locator('.home-tab:visible').count(),6,'Review keeps the six main tabs');assert.equal(await page.locator('.phoenix-sky').isVisible(),true);assert.equal(await page.locator('body').evaluate(e=>e.classList.contains('review-focus-host')),false,'Review must not cover the shared header');const reserved=await frame.locator('.review-actions').boundingBox();releaseImage();imageHold=null;await frame.locator('.review-chart img').evaluate(img=>img.decode());const loaded=await frame.locator('.review-actions').boundingBox();assert.ok(Math.abs(reserved.y-loaded.y)<2,'Loading a chart image must not move the decision row');await frame.locator('body').evaluate(()=>document.fonts.ready);
   const measurements=await frame.locator('#market-pane-review').evaluate(e=>{
    const box=s=>document.querySelector(s).getBoundingClientRect().toJSON();
    return {width:innerWidth,height:innerHeight,scroll:document.documentElement.scrollHeight,panel:box('#market-pane-review'),chart:box('.review-chart'),actions:box('.review-actions'),fields:[...document.querySelectorAll('.review-metrics b')].map(x=>x.textContent),reason:box('#reviewDetail>textarea'),overflow:document.documentElement.scrollWidth>innerWidth+1};
   });
   assert.ok(!measurements.overflow,'No horizontal overflow');assert.ok(measurements.chart.height>=100,'Chart must retain usable space');assert.ok(measurements.actions.bottom<=measurements.panel.bottom+1,'All decisions fit inside the review panel');assert.ok(measurements.reason.bottom<=measurements.actions.top,'Reason and actions must not overlap');assert.ok(measurements.fields.includes('0.9854')&&measurements.fields.includes('×20 — محدود به سقف ارز'),'Prices and leverage cap must remain visible');
   const filled=await frame.locator('.review-chart img').evaluate(img=>{const r=img.getBoundingClientRect();return {x:r.x,y:r.y,w:r.width,h:r.height};});assert.ok(filled.h>0);
   if(process.env.REVIEW_SCREENSHOTS)await page.screenshot({path:process.env.REVIEW_SCREENSHOTS+`/review-${width}.png`});
   assert.equal(await frame.locator('#closeReviewFocus').isVisible(),false,'No fullscreen review mode inside the common shell');
   assert.deepEqual(errors,[],'No runtime errors');console.log(`PASS compact review ${width}x${height}: popover, empty state, iframe focus, chart, all fields and actions fit.`);
  }
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1;});
