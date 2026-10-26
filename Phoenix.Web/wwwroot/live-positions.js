(() => {
 'use strict';
 const panel=document.getElementById('signal-pane-positions'),list=document.getElementById('positionsList'),status=document.getElementById('positionsStatus'),refresh=document.getElementById('refreshPositions');
 let busy=false,lastSuccess=null;
 const node=(tag,value,cls)=>Object.assign(document.createElement(tag),{textContent:value,className:cls||''});
 const number=value=>value==null?'—':Number(value).toLocaleString('en-US',{maximumFractionDigits:8});
 const signed=(value,digits=2)=>value==null?'—':(Number(value)>0?'+':'')+Number(value).toLocaleString('en-US',{maximumFractionDigits:digits,minimumFractionDigits:2});
 function render(items){
  const fragment=document.createDocumentFragment();
  for(const item of items){
   const p=item.position,card=node('article','','position-card'),top=node('div','','position-top');
   top.append(node('b',p.symbol),node('span',(p.side==='Buy'?'Long':'Short')+' · '+number(p.leverage)+'×','position-side'));card.append(top);
   const stage=item.riskFreeReached?'محدودهٔ ریسک‌فری · پلهٔ دوم':item.firstProtection?'ریسک‌فری · پلهٔ اول':item.signalId?'معاملهٔ فعال':item.matchedSignals>1?'پوزیشن تجمیعی بای‌بیت':'پوزیشن باز بای‌بیت';
   card.append(node('span',stage+(item.timeframe?' · تایم‌فریم '+item.timeframe:''),'position-stage'));
   const pnl=node('div','','position-pnl '+(p.unrealisedPnl==null||Number(p.unrealisedPnl)===0?'neutral':p.unrealisedPnl>0?'positive':'negative'));
   pnl.append(node('strong',signed(p.unrealisedPnl)+' USDT'),node('span',signed(p.returnPercent)+'%'));card.append(pnl);
   const metrics=node('dl','','position-metrics');
   for(const [label,value] of [['ورود میانگین',p.averagePrice],['قیمت مارک',p.markPrice],['مارجین اولیه',p.initialMargin],['اندازهٔ پوزیشن',p.size],['تارگت',p.takeProfit>0?p.takeProfit:null],['استاپ صرافی',p.stopLoss>0?p.stopLoss:null],['لیکویید',p.liquidationPrice>0?p.liquidationPrice:null]]){const cell=node('div','');cell.append(node('dt',label),node('dd',number(value)));metrics.append(cell);}
   card.append(metrics);fragment.append(card);
  }
  list.replaceChildren(fragment);if(!items.length)list.append(node('p','در حال حاضر پوزیشن بازی در بای‌بیت ندارید.','positions-empty'));
 }
 async function load(){
  if(busy||panel.hidden||document.hidden)return;busy=true;refresh.disabled=true;
  try{
   const response=await fetch('/api/positions/live',{cache:'no-store'});
   if(!response.ok)throw Error(response.status===403?'برای مشاهدهٔ حساب واقعی با حساب مالک وارد شو.':'ارتباط با بای‌بیت برقرار نشد.');
   const data=await response.json();render(data.items);lastSuccess=data.updatedAtUtc;
   status.textContent='آخرین دریافت: '+new Date(lastSuccess).toLocaleTimeString('fa-IR')+' · به‌روزرسانی هر ۵ ثانیه';
  }catch(error){status.textContent=error.message+(lastSuccess?' اطلاعات نمایش‌داده‌شده مربوط به '+new Date(lastSuccess).toLocaleTimeString('fa-IR')+' است.':' دوباره تلاش کن.');}
  finally{busy=false;refresh.disabled=false;}
 }
 refresh.addEventListener('click',load);window.addEventListener('phoenix-signal-tab-changed',event=>{if(event.detail==='positions')load();});
 document.addEventListener('visibilitychange',()=>{if(!document.hidden)load();});setInterval(load,5000);
})();
