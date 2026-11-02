const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),http=require('http'),{chromium}=require('./ui/node_modules/playwright-core'),lambda=require('./ui/node_modules/@sparticuz/chromium');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot');
(async()=>{
 const decoration=fs.readFileSync(root+'/crypto-ice.js','utf8').split('  const tabs=[')[0]+'})();';
 const html=fs.readFileSync(root+'/crypto-market.html','utf8').replace(/<script[\s\S]*?<\/script>/g,'').replace('</body>','<script>'+decoration+'</script><script src="/batch-picker.js"></script></body>');
 const server=http.createServer((req,res)=>{let url=new URL(req.url,'http://fixture').pathname;if(url==='/parent'){res.setHeader('Content-Type','text/html');return res.end('<iframe id="form" src="/fixture" style="width:100%;height:600px;border:0"></iframe>');}if(url==='/fixture'){res.setHeader('Content-Type','text/html');return res.end(html);}const f=path.resolve(root,'.'+url);if(!f.startsWith(path.resolve(root)+path.sep)||!fs.existsSync(f)){res.writeHead(404).end();return;}res.setHeader('Content-Type',url.endsWith('.js')?'application/javascript':url.endsWith('.css')?'text/css':'application/octet-stream');res.end(fs.readFileSync(f));});await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:process.env.TEST_CHROMIUM_EXECUTABLE||await lambda.executablePath(),args:lambda.args,headless:true});const page=await browser.newPage({viewport:{width:390,height:780}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 try{
 for(const route of ['/fixture','/parent']){
  await page.goto('http://127.0.0.1:'+server.address().port+route);const form=route==='/parent'?page.frameLocator('#form'):page;
  const trigger=form.getByRole('button',{name:'نوع نمودار',exact:true});assert.equal(await form.locator('.batch-picker-trigger').count(),4);assert.equal(await form.locator('#batchChart').isVisible(),false);
  await form.locator('#batchChart').evaluate(e=>{window.changeCount=0;e.addEventListener('change',()=>window.changeCount++);});
  await trigger.click();const dialog=page.getByRole('dialog',{name:'نوع نمودار',exact:true});await dialog.waitFor();assert.ok(await dialog.evaluate(e=>getComputedStyle(e).backgroundColor==='rgb(4, 23, 44)'));assert.ok(await dialog.evaluate(e=>e.getBoundingClientRect().width<=300));
  await dialog.getByRole('option',{name:'فقط کندل‌استیک',exact:true}).click();assert.equal(await form.locator('#batchChart').inputValue(),'Candles');assert.equal(await form.locator('#batchChart').evaluate(()=>window.changeCount),1);assert.ok((await trigger.textContent()).includes('فقط کندل‌استیک'));
  await trigger.click();await page.keyboard.press('Escape');assert.equal(await form.locator('#batchChart').inputValue(),'Candles');assert.equal(await trigger.getAttribute('aria-expanded'),'false');
  await form.getByRole('button',{name:'تایم‌فریم',exact:true}).click();await page.keyboard.press('End');await page.keyboard.press('Enter');assert.equal(await form.locator('#batchTimeframe').inputValue(),'240');
  const duration=form.getByRole('button',{name:'مدت جست‌وجو',exact:true});assert.equal(await duration.isDisabled(),true);await form.locator('#batchDuration').evaluate(e=>{e.disabled=false});assert.equal(await duration.isEnabled(),true);await duration.click();await page.getByRole('dialog').getByRole('button',{name:'بستن',exact:true}).click();
  console.log('PASS themed select '+route+': native values/events, parent modal, compact navy theme, Escape, keyboard navigation and disabled state');
 }
 assert.deepEqual(errors,[]);
 }finally{await browser.close();await new Promise(r=>server.close(r));}
})().catch(e=>{console.error(e);process.exitCode=1});
