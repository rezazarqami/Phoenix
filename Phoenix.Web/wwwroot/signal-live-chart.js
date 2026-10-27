(() => {
 'use strict';const charts=new Set();
 const colors={CEILING:'#d8ae37',FLOOR:'#a37aca',ENTRY:'#359bea',TP:'#19aa83',SL:'#e06c84',RF:'#39b8b1','RF1 SL':'#6f9fcd','RF2 SL':'#67b49a'};
 window.PhoenixLiveChart={mount(host,id){
  const caption=document.createElement('div'),canvas=document.createElement('div');caption.className='signal-live-status';canvas.className='signal-live-canvas';caption.textContent='دریافت نمودار زنده…';host.replaceChildren(caption,canvas);
  let chart,series,priceLine,lines=[],levelKey='',lastAt=null,disposed=false,busy=false,controller,resize;
  const state={host,stop(){if(disposed)return;disposed=true;controller?.abort();resize?.disconnect();chart?.remove();charts.delete(state);},load};charts.add(state);
  function visible(){if(document.hidden||host.closest('[hidden]'))return false;for(let el=host.parentElement;el;el=el.parentElement)if(el.tagName==='DETAILS'&&!el.open)return false;const r=host.getBoundingClientRect();return r.bottom>=0&&r.top<=innerHeight;}
  async function load(first=false){
   if(disposed||busy||(!first&&!visible()))return;busy=true;controller=new AbortController();
   try{
    const response=await fetch('/api/signals/'+encodeURIComponent(id)+'/live-chart',{cache:'no-store',signal:controller.signal});if(!response.ok)throw Error();const data=await response.json();if(disposed)return;if(!host.isConnected){state.stop();return;}
    const candles=data.candles.map(c=>({time:Math.floor(Number(c.openTime)/1000),open:Number(c.open),high:Number(c.high),low:Number(c.low),close:Number(c.close)})).sort((a,b)=>a.time-b.time).filter((c,i,a)=>i===0||c.time!==a[i-1].time);
    if(!candles.length||!window.LightweightCharts)throw Error();
    if(!chart){chart=LightweightCharts.createChart(canvas,{width:Math.max(180,canvas.clientWidth),height:260,localization:{locale:'en-US'},layout:{background:{color:'#ffffff'},textColor:'#7c91a3',fontSize:10},grid:{vertLines:{color:'#f0f5f8'},horzLines:{color:'#f0f5f8'}},rightPriceScale:{mode:LightweightCharts.PriceScaleMode.Logarithmic,borderColor:'#e6eff5'},timeScale:{timeVisible:true,borderColor:'#e6eff5'},handleScroll:{vertTouchDrag:false}});series=chart.addCandlestickSeries({upColor:'#35b89b',downColor:'#e5808c',borderVisible:false,wickUpColor:'#35b89b',wickDownColor:'#e5808c',priceFormat:{type:'price',precision:8,minMove:0.00000001}});resize=new ResizeObserver(()=>chart.resize(Math.max(180,canvas.clientWidth),260));resize.observe(canvas);}
    const precision=Math.min(8,Math.max(2,...data.levels.map(l=>l.price).concat(data.lastPrice).map(value=>{const [digits,exponent]=String(Number(value)).toLowerCase().split('e');return Math.max(0,(digits.split('.')[1]?.length||0)-Number(exponent||0));})));series.applyOptions({priceFormat:{type:'price',precision,minMove:10**(-precision)}});
    const previous=chart.timeScale().getVisibleLogicalRange();series.setData(candles);
    const key=JSON.stringify(data.levels);if(key!==levelKey){for(const line of lines)series.removePriceLine(line);lines=data.levels.map(l=>series.createPriceLine({price:Number(l.price),color:colors[l.label]||'#8ab4cb',lineWidth:1,lineStyle:0,axisLabelVisible:true,title:l.label}));levelKey=key;}
    if(priceLine)series.removePriceLine(priceLine);priceLine=series.createPriceLine({price:Number(data.lastPrice),color:'#176fa7',lineWidth:1,lineStyle:2,axisLabelVisible:true,title:'NOW'});
    // Keep the original signal levels visible without changing any of their values.
    series.applyOptions({autoscaleInfoProvider:original=>{const info=original();if(!info)return null;const values=data.levels.map(l=>Number(l.price)).concat(Number(data.lastPrice));return {...info,priceRange:{minValue:Math.min(info.priceRange.minValue,...values),maxValue:Math.max(info.priceRange.maxValue,...values)}};}});
    if(!lastAt)chart.timeScale().fitContent();else if(previous)chart.timeScale().setVisibleLogicalRange(previous);
    lastAt=data.updatedAtUtc;caption.textContent=data.symbol+' · '+Number(data.lastPrice).toLocaleString('en-US',{maximumFractionDigits:8})+' · کندل '+data.interval+' · '+new Date(lastAt).toLocaleTimeString('fa-IR')+' · به‌روزرسانی ۵ ثانیه‌ای';caption.dataset.stale='false';
   }catch(error){if(disposed||error.name==='AbortError')return;caption.dataset.stale='true';caption.textContent=lastAt?'ارتباط قطع شد؛ نمودار مربوط به '+new Date(lastAt).toLocaleTimeString('fa-IR')+' است.':'دریافت نمودار زنده انجام نشد؛ دوباره بخش نمودار را باز کن.';}
   finally{busy=false;}
  }
  load(true);return state;
 }};
 setInterval(()=>{for(const state of charts){if(!state.host.isConnected)state.stop();else state.load();}},5000);
 document.addEventListener('visibilitychange',()=>{if(!document.hidden)for(const state of charts)state.load();});
})();
