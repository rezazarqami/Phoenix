const assert=require('node:assert/strict'),fs=require('fs'),path=require('path'),{JSDOM}=require('./ui/node_modules/jsdom');
const root=path.join(__dirname,'../Phoenix.Web/wwwroot'),html=fs.readFileSync(root+'/phoenix.html','utf8'),script=fs.readFileSync(root+'/home-tabs.js','utf8');
for(const hash of ['','#notifications?eventId=event-123&source=manual&view=results']){
 const dom=new JSDOM(html,{url:'https://phoenix.test/phoenix'+hash,runScripts:'outside-only',pretendToBeVisual:true}),w=dom.window,d=w.document,errors=[];
 w.scrollTo=()=>{};w.ResizeObserver=class{observe(){}disconnect(){}};w.addEventListener('error',e=>errors.push(e.message));
 assert.doesNotThrow(()=>w.eval(script),'Routing must initialize after the subtitle is removed');
 assert.equal(d.querySelector('#createHeading>p'),null,'Use the redesigned markup without its old subtitle');
 if(hash){const url=new URL(d.querySelector('#journalFrameHost iframe').src);assert.equal(url.searchParams.get('eventId'),'event-123');assert.equal(url.searchParams.get('source'),'manual');assert.equal(url.searchParams.get('view'),'results');}
 d.querySelector('#tab-notifications').click();assert.ok(d.querySelector('#journalFrameHost iframe'),'Clicking Events must create the journal iframe');assert.equal(d.querySelector('#panel-notifications').hidden,false);
 for(const name of ['home','crypto','tracking','more','notifications']){d.querySelector('#tab-'+name).click();assert.equal(d.querySelector('#tab-'+name).getAttribute('aria-selected'),'true');}
 d.querySelector('#tab-crypto').click();assert.equal(d.querySelector('#createHeading h1').textContent,'ایجاد سیگنال جدید');assert.ok(d.querySelector('#cryptoFrameHost iframe'));
 assert.deepEqual(errors,[],'No navigation exception should leave an empty panel');dom.window.close();
}
console.log('PASS navigation regression: removed subtitle, every main tab, event deep links and journal frame creation.');
