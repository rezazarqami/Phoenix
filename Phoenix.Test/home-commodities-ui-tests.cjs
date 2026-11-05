const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM}=require('./ui/node_modules/jsdom');
(async()=>{
 const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),dom=new JSDOM(fs.readFileSync(path.join(root,'phoenix.html'),'utf8'),{url:'https://phoenix.test/phoenix',runScripts:'outside-only'}),w=dom.window,d=w.document;
 Object.defineProperty(d,'hidden',{value:false});w.setInterval=()=>0;const flush=()=>new Promise(r=>setImmediate(r));let calls=0,fail=false,partial=false;
 w.fetch=async(url)=>{if(url.endsWith('/history'))return{ok:true,json:async()=>({items:[]})};calls++;if(fail)throw Error('offline');return{ok:true,json:async()=>({items:[{symbol:'XAU',price:partial?null:3000.50,atUtc:'2026-10-30T12:00:00Z',source:'Gold API'},{symbol:'BRENT',price:partial?81:80.25,atUtc:'2026-10-30T12:00:00Z',source:'Yahoo Finance',delayed:true},{symbol:'USD_TOMAN',price:271500,currency:'TOMAN',atUtc:'2026-10-30T12:00:00Z',source:'TGJU'},{symbol:'GOLD18_TOMAN',price:25513400,currency:'TOMAN',atUtc:'2026-10-30T12:00:00Z',source:'TGJU'},{symbol:'EUR_TOMAN',price:303830,currency:'TOMAN',atUtc:'2026-10-30T12:00:00Z',source:'TGJU'}]})};};
 w.eval(fs.readFileSync(path.join(root,'home-commodities.js'),'utf8'));await flush();
 assert.equal(d.getElementById('goldPrice').textContent,'3,000.50');assert.equal(d.getElementById('brentPrice').textContent,'80.25');assert.equal(d.getElementById('goldTime').dateTime,'2026-10-30T12:00:00.000Z');
 assert.equal(d.getElementById('dollarPrice').textContent,new Intl.NumberFormat('en-US').format(271500));assert.equal(d.getElementById('gold18Price').textContent,new Intl.NumberFormat('en-US').format(25513400),'Browser must not divide already converted rates again');
 const update=()=>w.dispatchEvent(new w.Event('phoenix-tab-changed'));d.getElementById('panel-home').hidden=true;update();await flush();assert.equal(calls,1,'Hidden homepage does not fetch commodities');d.getElementById('panel-home').hidden=false;
 fail=true;update();await flush();assert.equal(d.getElementById('goldPrice').textContent,'3,000.50');assert.equal(d.getElementById('goldStatus').dataset.error,'true','Failure must be visible without inventing a new price');
 fail=false;partial=true;update();await flush();assert.equal(d.getElementById('brentPrice').textContent,'81.00','Unavailable gold must not suppress a successful Brent update');assert.equal(d.getElementById('goldStatus').dataset.error,'true');
 assert.equal(d.getElementById('goldChange').textContent,'—','A single quote must not invent a percent change');assert.equal(d.getElementById('goldSpark').querySelector('.quote-line').hasAttribute('d'),false,'No synthetic history on first load');
 // A fresh page can draw all five archives without accumulating browser-session ticks.
 w.fetch=async(url)=>url.endsWith('/history')?{ok:true,json:async()=>({items:['XAU','BRENT','USD_TOMAN','GOLD18_TOMAN','EUR_TOMAN'].map(symbol=>({symbol,points:[{atUtc:'2026-10-28T12:00:00Z',value:100},{atUtc:'2026-10-29T12:00:00Z',value:110}]}))})}:{ok:false};
 w.eval(fs.readFileSync(path.join(root,'home-commodities.js'),'utf8'));await flush();
 // Load current values separately after the archive has arrived.
 w.fetch=async()=>({ok:true,json:async()=>({items:[{symbol:'XAU',price:120,atUtc:'2026-10-30T12:00:00Z',source:'Gold API'}]})});update();await flush();
 assert.match(d.getElementById('goldSpark').querySelector('.quote-line').getAttribute('d'),/^M/);
 assert.equal(d.getElementById('goldChange').textContent,'↑ +20.00%','Percent uses the same archive window as the chart');
 dom.window.close();console.log('PASS commodity cards: exact units, upstream timestamps, hidden-page polling and independent failure handling');
})().catch(e=>{console.error(e);process.exitCode=1;});
