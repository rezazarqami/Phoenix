const fa=new Intl.NumberFormat('fa-IR',{maximumFractionDigits:6});const els={symbol:document.querySelector('#analysisSymbol'),menu:document.querySelector('#analysisSymbolMenu'),interval:document.querySelector('#analysisInterval'),type:document.querySelector('#chartType'),depth:document.querySelector('#pivotDepth'),deviation:document.querySelector('#pivotDeviation'),button:document.querySelector('#analyzeButton'),chart:document.querySelector('#chart'),scenarios:document.querySelector('#scenarios'),message:document.querySelector('#analysisMessage'),active:document.querySelector('#activeSymbol'),activeInterval:document.querySelector('#activeInterval'),price:document.querySelector('#latestPrice')};let instruments=[],chart,series,markers=[],currentData;

function createChart(){if(chart)chart.remove();els.chart.innerHTML='';chart=LightweightCharts.createChart(els.chart,{width:els.chart.clientWidth,height:els.chart.clientHeight,layout:{background:{color:'#0d0b08'},textColor:'#9d9078',fontFamily:'Arial'},grid:{vertLines:{color:'#211a10'},horzLines:{color:'#211a10'}},rightPriceScale:{borderColor:'#493a23'},timeScale:{borderColor:'#493a23',timeVisible:true,secondsVisible:false},crosshair:{vertLine:{color:'#8b6a32'},horzLine:{color:'#8b6a32'}}});series=els.type.value==='line'?chart.addLineSeries({color:'#38d39f',lineWidth:2}):chart.addCandlestickSeries({upColor:'#26a882',downColor:'#e05267',borderVisible:false,wickUpColor:'#38d39f',wickDownColor:'#f06b7e'});window.addEventListener('resize',resizeChart,{once:true})}function resizeChart(){if(chart)chart.applyOptions({width:els.chart.clientWidth,height:els.chart.clientHeight});window.addEventListener('resize',resizeChart,{once:true})}

async function loadInstruments(){try{const response=await fetch('/api/analysis/instruments');if(response.status===401)return location.replace('/login');const data=await response.json();instruments=data.symbols||[]}catch{instruments=[]}}
function renderMenu(){const query=els.symbol.value.trim().toUpperCase();const matches=instruments.filter(x=>x.includes(query)).slice(0,120);els.menu.innerHTML=matches.map(x=>`<button type="button" data-symbol="${x}"><b>${x}</b><small>Bybit Linear</small></button>`).join('')||'<div class="empty">نمادی پیدا نشد.</div>';els.menu.hidden=false}
els.symbol.addEventListener('input',()=>{els.symbol.value=els.symbol.value.toUpperCase().replace(/[^A-Z0-9-]/g,'');renderMenu()});els.symbol.addEventListener('focus',renderMenu);document.querySelector('#symbolToggle').addEventListener('click',renderMenu);els.menu.addEventListener('click',event=>{const button=event.target.closest('button[data-symbol]');if(button){els.symbol.value=button.dataset.symbol;els.menu.hidden=true;analyze()}});document.addEventListener('click',event=>{if(!event.target.closest('.symbol-control'))els.menu.hidden=true});

function setSeriesData(candles){if(els.type.value==='line')series.setData(candles.map(c=>({time:c.openTime/1000,value:Number(c.close)})));else series.setData(candles.map(c=>({time:c.openTime/1000,open:Number(c.open),high:Number(c.high),low:Number(c.low),close:Number(c.close)})))}
function showScenario(index){
  const scenarios=currentData?.analysis?.scenarios||[],selected=scenarios[index];
  document.querySelectorAll('.scenario').forEach((node,i)=>node.classList.toggle('active',i===index));
  markers=[];
  if(selected){
    const candles=currentData.candles||[],positions=new Map(candles.map((c,i)=>[c.openTime,i])),seen=new Set();
    const roots=selected.coverage?.sections||[];
    const waves=[...(selected.validationStatus==='Verified'?selected.waves:[]),
      ...(selected.contextWaves||[]).filter(w=>w.validationStatus==='Verified'),
      ...(selected.subwaves||[]).filter(w=>w.degree<=1&&w.validationStatus==='Verified'&&
        (w.origin==='Subdivision'&&roots.some(s=>w.parent?.startsWith(s.id+'/'))||
         selected.validationStatus==='Verified'&&w.origin!=='Continuation'))];
    markers=waves.filter(w=>{
      const key=`${w.time}:${w.label}:${w.degree||0}`;
      if(w.label==='0'||!positions.has(w.time)||seen.has(key))return false;
      seen.add(key);return true;
    }).map(w=>{
      const i=positions.get(w.time),above=i===0?Number(w.price)>=Number(candles[1]?.close??w.price):Number(w.price)>=Number(candles[i-1].close);
      const degree=Math.max(0,w.degree||0),label=degree===0?`(${w.label})`:degree===1?w.label:`(${w.label.toLowerCase()})`;
      return {time:w.time/1000,position:above?'aboveBar':'belowBar',color:degree===0?'#f0c94d':degree===1?'#80aaff':'#ef8796',shape:'circle',size:degree===0?.7:.4,text:label+(w.isTentative?'?':'')};
    }).sort((a,b)=>a.time-b.time);
  }else markers=(currentData?.analysis?.pivots||[]).slice(-20).map(p=>({time:p.time/1000,position:p.kind==='High'?'aboveBar':'belowBar',color:'#60746e',shape:'circle',text:''}));
  series.setMarkers(markers);
}
const patternFa=value=>({Impulse:'ایمپالس',HarmonicImpulse:'الیوت تعدیل‌شده کوپسی',LeadingDiagonal:'دیاگونال آغازین',DoubleThree:'اصلاح دوتایی',TripleThree:'اصلاح سه‌تایی',DevelopingImpulse:'ایمپالس در حال تشکیل',TruncatedImpulse:'موج پنجم ناقص',EndingDiagonal:'دیاگونال پایانی',DevelopingDiagonal:'دیاگونال در حال تشکیل',Zigzag:'زیگزاگ',Flat:'فلت',ExpandedFlat:'فلت گسترش‌یافته',RunningFlat:'فلت رانینگ',ContractingTriangle:'مثلث همگرا',ExpandingTriangle:'مثلث واگرا'}[value]||value);
const gapReasonFa = reason => ({InsufficientCandles:'کندل کافی نیست',DataUnavailable:'داده دریافت نشد',SearchLimit:'بررسی به سقف جست‌وجو رسید',ParentUnknown:'جایگاه موج والد تأیید نشده',SubdivisionUnknown:'ریزساختار تأیید نشده',NoValidStructure:'ساختار معتبر پیدا نشد'}[reason] || 'تأیید نشده');
function coverageHtml(report) {
  if (!report) return '';
  const sections = report.sections || [], gaps = report.gaps || [];
  const time = value => new Date(value).toLocaleString('fa-IR');
  return `<p>پوشش تأییدشدهٔ نمودار: <b>${fa.format(report.verifiedPercent)}٪</b> · ${fa.format(sections.length)} بخش مستقل · ${fa.format(gaps.length)} ناحیهٔ تأییدنشده</p>
    <details><summary>ساختارها و علت نواحی خالی</summary>
    <p>بخش‌های تاریخی شمارش‌های مستقل‌اند؛ رنگ یا تایم‌فریم به‌تنهایی رابطهٔ والد و ریزموج را تأیید نمی‌کند.</p>
    ${sections.map((section, i) => `<p>بخش ${fa.format(i + 1)}: ${patternFa(section.pattern)} · ${time(section.start)} تا ${time(section.end)}</p>`).join('')}
    ${gaps.map(gap => `<p>${time(gap.start)} تا ${time(gap.end)}: ${gapReasonFa(gap.reason)}</p>`).join('')}
    ${(report.dataIssues || []).map(issue => { const [tier, reason] = issue.split(':'); return `<p>${tier}: ${gapReasonFa(reason)}</p>`; }).join('')}
    </details>`;
}
function renderScenarios(analysis){document.querySelector('.aside-head small').textContent=`نسخه قوانین ${analysis.ruleSetVersion||'۲.۰'}`;if(!analysis.scenarios.length){els.scenarios.innerHTML='<div class="empty">ساختار معتبر کافی پیدا نشد. پیوت‌های شناسایی‌شده روی نمودار مشخص شده‌اند.</div>'+coverageHtml(analysis.coverage);showScenario(-1);return}els.scenarios.innerHTML=analysis.scenarios.map((s,index)=>`<article class="scenario${index===0?' active':''}" data-index="${index}"><div class="scenario-top"><h3>سناریوی ${fa.format(index+1)} · <b>${s.direction==='Bullish'?'صعودی':'نزولی'}</b></h3><span class="score">${fa.format(s.score)}</span></div><p><b>${patternFa(s.pattern)}</b> · ${s.currentWave}</p><p>${s.summary}</p>${coverageHtml(s.coverage)}<p>اعتبار: ${s.validationStatus==='Verified'?'ریزساختار تأییدشده':'شمارش اولیه؛ ریزساختار کامل تأیید نشده'} · ${s.phase==='Developing'?'پایان در حال تشکیل':'ساختار تاریخی'}</p><p>روش: ${s.profile||'Classic'} · پوشش ریزساختار: ${fa.format(s.subdivisionCoveragePercent||0)}٪</p><div class="ratio"><span>R2 ${s.ratios.wave2Retracement}</span><span>E3/C ${s.ratios.wave3Extension}</span><span>R4 ${s.ratios.wave4Retracement}</span></div><div class="rules">${s.rules.map(r=>`<div class="rule ${r.passed?'pass':''}"><i>${r.status==='Unknown'?'؟':r.passed?'✓':'×'}</i><span>${r.isHard?'قانون سخت':'راهنما'}: ${r.description}</span></div>`).join('')}</div><div class="invalidation">ابطال شمارش: <b>${fa.format(s.startInvalidation)}</b></div></article>`).join('');showScenario(0)}
els.scenarios.addEventListener('click',event=>{const scenario=event.target.closest('.scenario');if(scenario)showScenario(Number(scenario.dataset.index))});

async function analyze(){const symbol=els.symbol.value.trim().toUpperCase();if(!symbol)return;els.button.disabled=true;els.button.textContent='در حال تحلیل…';els.message.textContent='دریافت ۵۰۰ کندل و استخراج پیوت‌های مهم…';try{const query=new URLSearchParams({symbol,interval:els.interval.value,limit:'500',depth:els.depth.value,deviation:els.deviation.value});const response=await fetch('/api/analysis/candles?'+query);if(response.status===401)return location.replace('/login');const data=await response.json();if(!response.ok)throw new Error(data.error||'تحلیل ناموفق بود.');currentData=data;createChart();setSeriesData(data.candles);renderScenarios(data.analysis);chart.timeScale().fitContent();els.active.textContent=data.symbol;els.activeInterval.textContent=els.interval.options[els.interval.selectedIndex].text+' · Bybit Linear';const last=data.candles[data.candles.length-1];els.price.textContent=last?fa.format(last.close):'—';els.message.textContent=data.analysis.message}catch(error){els.message.textContent=error.message;els.scenarios.innerHTML='<div class="empty">امکان تکمیل تحلیل وجود نداشت.</div>'}finally{els.button.disabled=false;els.button.textContent='تحلیل مجدد'}}
els.button.addEventListener('click',analyze);els.interval.addEventListener('change',analyze);els.type.addEventListener('change',()=>{if(currentData){createChart();setSeriesData(currentData.candles);showScenario(0);chart.timeScale().fitContent()}});document.querySelector('#analysisLogout').addEventListener('click',async()=>{await fetch('/api/analysis/auth/logout',{method:'POST'});location.replace('/login')});loadInstruments().then(analyze);
