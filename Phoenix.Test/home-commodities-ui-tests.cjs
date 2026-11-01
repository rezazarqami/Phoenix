const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM}=require('./ui/node_modules/jsdom');
(async()=>{
 const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),dom=new JSDOM(fs.readFileSync(path.join(root,'phoenix.html'),'utf8'),{url:'https://phoenix.test/phoenix',runScripts:'outside-only'}),w=dom.window,d=w.document;
 Object.defineProperty(d,'hidden',{value:false});w.setInterval=()=>0;const flush=()=>new Promise(r=>setImmediate(r));let calls=0,fail=false,partial=false;
 w.fetch=async()=>{calls++;if(fail)throw Error('offline');return{ok:true,json:async()=>({items:[{symbol:'XAU',price:partial?null:3000.50,atUtc:'2026-10-30T12:00:00Z',source:'Gold API'},{symbol:'BRENT',price:partial?81:80.25,atUtc:'2026-10-30T12:00:00Z',source:'Yahoo Finance',delayed:true}]})};};
 w.eval(fs.readFileSync(path.join(root,'home-commodities.js'),'utf8'));await flush();
 assert.equal(d.getElementById('goldPrice').textContent,'3,000.50 USD');assert.equal(d.getElementById('brentPrice').textContent,'80.25 USD');assert.equal(d.getElementById('goldTime').dateTime,'2026-10-30T12:00:00.000Z');
 const update=()=>w.dispatchEvent(new w.Event('phoenix-tab-changed'));d.getElementById('panel-home').hidden=true;update();await flush();assert.equal(calls,1,'Hidden homepage does not fetch commodities');d.getElementById('panel-home').hidden=false;
 fail=true;update();await flush();assert.equal(d.getElementById('goldPrice').textContent,'3,000.50 USD');assert.equal(d.getElementById('goldStatus').dataset.error,'true','Failure must be visible without inventing a new price');
 fail=false;partial=true;update();await flush();assert.equal(d.getElementById('brentPrice').textContent,'81.00 USD','Unavailable gold must not suppress a successful Brent update');assert.equal(d.getElementById('goldStatus').dataset.error,'true');
 dom.window.close();console.log('PASS commodity cards: exact units, upstream timestamps, hidden-page polling and independent failure handling');
})().catch(e=>{console.error(e);process.exitCode=1;});
