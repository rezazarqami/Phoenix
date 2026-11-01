(() => {
 'use strict';const charts=new Set();
 const colors={CEILING:'#d8ae37',FLOOR:'#a37aca',ENTRY:'#359bea',TP:'#19aa83',SL:'#e06c84',RF:'#39b8b1','RF1 SL':'#6f9fcd','RF2 SL':'#67b49a'};
 window.PhoenixLiveChart={mount(host,id,strategy){
  const caption=document.createElement('div'),canvas=document.createElement('div');caption.className='signal-live-status';canvas.className='signal-live-canvas';caption.textContent='دریافت نمودار زنده…';const reset=document.createElement('button');reset.type='button';reset.className='signal-live-reset';reset.textContent='تنظیم خودکار نمودار';reset.addEventListener('click',()=>{if(!chart)return;chart.priceScale('right').applyOptions({autoScale:true});chart.timeScale().fitContent();});host.replaceChildren(caption,reset,canvas);
  let chart,series,priceLine,lines=[],levelKey='',lastAt=null,disposed=false,busy=false,controller,resize;
  const state={host,stop(){if(disposed)return;disposed=true;controller?.abort();resize?.disconnect();chart?.remove();charts.delete(state);},load};charts.add(state);
  function visible(){if(document.hidden||host.closest('[hidden]'))return false;for(let el=host.parentElement;el;el=el.parentElement)if(el.tagName==='DETAILS'&&!el.open)return false;const r=host.getBoundingClientRect();return r.bottom>=0&&r.top<=innerHeight;}
  async function load(first=false){
   if(disposed||busy||(!first&&!visible()))return;busy=true;controller=new AbortController();
   try{
    const response=await fetch('/api/signals/'+encodeURIComponent(id)+'/live-chart'+(strategy==='strategy2'?'?strategy=strategy2':''),{cache:'no-store',signal:controller.signal});if(!response.ok)throw Error();const data=await response.json();if(disposed)return;if(!host.isConnected){state.stop();return;}
    const candles=data.candles.map(c=>({time:Math.floor(Number(c.openTime)/1000),open:Number(c.open),high:Number(c.high),low:Number(c.low),close:Number(c.close)})).sort((a,b)=>a.time-b.time).filter((c,i,a)=>i===0||c.time!==a[i-1].time);
    if(!candles.length||!window.LightweightCharts)throw Error();
    if(!chart){chart=LightweightCharts.createChart(canvas,{width:Math.max(180,canvas.clientWidth),height:260,localization:{locale:'en-US'},layout:{background:{color:'#061a37'},textColor:'#b4c6e4',fontSize:10},grid:{vertLines:{color:'#123457'},horzLines:{color:'#123457'}},rightPriceScale:{mode:LightweightCharts.PriceScaleMode.Logarithmic,autoScale:true,scaleMargins:{top:0.12,bottom:0.12},borderColor:'#21598b',minimumWidth:76},timeScale:{timeVisible:true,borderColor:'#21598b'},handleScale:{axisPressedMouseMove:{price:true,time:true},axisDoubleClickReset:{price:true,time:true},mouseWheel:true,pinch:true},handleScroll:{vertTouchDrag:true,horzTouchDrag:true}});series=chart.addCandlestickSeries({upColor:'#35b89b',downColor:'#e5808c',borderVisible:false,wickUpColor:'#35b89b',wickDownColor:'#e5808c',priceFormat:{type:'price',precision:8,minMove:0.00000001}});resize=new ResizeObserver(()=>chart.resize(Math.max(180,canvas.clientWidth),260));resize.observe(canvas);}
    const precision=Math.min(8,Math.max(2,...data.levels.map(l=>l.price).concat(data.lastPrice).map(value=>{const [digits,exponent]=String(Number(value)).toLowerCase().split('e');return Math.max(0,(digits.split('.')[1]?.length||0)-Number(exponent||0));})));series.applyOptions({priceFormat:{type:'price',precision,minMove:10**(-precision)}});
    const previous=chart.timeScale().getVisibleLogicalRange();series.setData(candles);
    const key=JSON.stringify(data.levels);if(key!==levelKey){for(const line of lines)series.removePriceLine(line);lines=data.levels.map(l=>series.createPriceLine({price:Number(l.price),color:colors[l.label]||'#8ab4cb',lineWidth:1,lineStyle:0,axisLabelVisible:true,title:l.label}));levelKey=key;}
    if(priceLine)series.removePriceLine(priceLine);priceLine=series.createPriceLine({price:Number(data.lastPrice),color:'#176fa7',lineWidth:1,lineStyle:2,axisLabelVisible:true,title:'NOW'});
    // Autoscale only visible candles. Distant historical levels must not flatten live prices.
    // Leave the price scale untouched during polling so axis-drag zoom persists.
    if(!lastAt)chart.timeScale().fitContent();else if(previous)chart.timeScale().setVisibleLogicalRange(previous);
    lastAt=data.updatedAtUtc;caption.textContent=data.symbol+' · '+Number(data.lastPrice).toLocaleString('en-US',{maximumFractionDigits:8})+' · کندل '+data.interval+' · '+new Date(lastAt).toLocaleTimeString('fa-IR')+' · لگاریتمی · برای زوم عمودی، اعداد قیمت را بالا و پایین بکشید · به‌روزرسانی ۵ ثانیه‌ای';caption.dataset.stale='false';
   }catch(error){if(disposed||error.name==='AbortError')return;caption.dataset.stale='true';caption.textContent=lastAt?'ارتباط قطع شد؛ نمودار مربوط به '+new Date(lastAt).toLocaleTimeString('fa-IR')+' است.':'دریافت نمودار زنده انجام نشد؛ دوباره بخش نمودار را باز کن.';}
   finally{busy=false;}
  }
  load(true);return state;
 }};
 setInterval(()=>{for(const state of charts){if(!state.host.isConnected)state.stop();else state.load();}},5000);
 document.addEventListener('visibilitychange',()=>{if(!document.hidden)for(const state of charts)state.load();});
})();
