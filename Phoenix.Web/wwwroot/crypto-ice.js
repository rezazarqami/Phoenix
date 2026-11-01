(() => {
  'use strict';
  const paths={send:'m22 2-7 20-4-9-9-4 20-7ZM22 2 11 13',shield:'M12 2 3 6v6c0 5 9 10 9 10s9-5 9-10V6l-9-4Zm-4 10 3 3 5-6',database:'M20 6c0 2-4 4-8 4S4 8 4 6s4-4 8-4 8 2 8 4ZM4 6v12c0 5 16 5 16 0V6M4 12c0 5 16 5 16 0',archive:'M4 7h16v14H4V7Zm-1-4h18v4H3V3Zm6 10h6m-3-2v4',chart:'M4 3h16v18H4V3Zm3 12 4-5 4 3 3-5',search:'M20 20l-5-5M17 9a7 7 0 1 1-14 0 7 7 0 0 1 14 0Z',bars:'M4 21v-7h3v7M11 21V9h3v12M18 21V3h3v18',clock:'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0ZM12 6v6l4 2',direction:'M3 7h18m-5-5 5 5-5 5M21 17H3m5-5-5 5 5 5',candles:'M5 2v20M2 7h6v9H2V7Zm10-6v22M9 4h6v9H9V4Zm10-2v20M16 9h6v8h-6V9Z',stop:'M6 4h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z'};
  const icon=name=>`<svg viewBox="0 0 24 24" fill="${name==='stop'?'currentColor':'none'}" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${paths[name]}"/></svg>`;
  document.querySelectorAll('[data-market-icon]').forEach(el=>el.innerHTML=icon(el.dataset.marketIcon));
  const heading=document.querySelector('.batch-box>div:first-child');
  heading.insertAdjacentHTML('afterbegin',`<i class="queue-icon">${icon('send')}</i>`);
  const fields={batchCount:'bars',batchDuration:'clock',batchSize:'database',batchDirection:'direction',batchChart:'candles',batchTimeframe:'clock'};
  for(const [id,name] of Object.entries(fields)){
    const input=document.getElementById(id),label=input.closest('label');
    const text=[...label.childNodes].find(n=>n.nodeType===3&&n.textContent.trim());
    const caption=document.createElement('span');caption.className='field-label';caption.textContent=text.textContent.trim();text.replaceWith(caption);
    label.insertAdjacentHTML('afterbegin',`<span class="field-icon">${icon(name)}</span>`);
    if(input.type==='number'){
      const control=document.createElement('span');control.className='number-control';input.before(control);control.append(input);
      for(const [delta,symbol] of [[-1,'−'],[1,'+']]){
        const button=document.createElement('button');button.type='button';button.textContent=symbol;button.setAttribute('aria-label',(delta<0?'کاهش ':'افزایش ')+caption.textContent);
        button.addEventListener('click',()=>{if(input.disabled)return;delta<0?input.stepDown():input.stepUp();input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));});control.append(button);
        new MutationObserver(()=>button.disabled=input.disabled).observe(input,{attributes:true,attributeFilter:['disabled']});button.disabled=input.disabled;
      }
    }
  }
  const timed=document.querySelector('.timed-toggle');timed.prepend(Object.assign(document.createElement('span'),{className:'field-icon',innerHTML:icon('clock')}));timed.append(document.getElementById('batchTimedMode'));
  const start=document.getElementById('startBatch');
  function decorateStart(){if(!start.querySelector('svg'))start.insertAdjacentHTML('afterbegin',icon('send'));}
  new MutationObserver(decorateStart).observe(start,{childList:true});decorateStart();
  document.getElementById('stopBatch').insertAdjacentHTML('afterbegin',icon('stop'));
  const tabs=[...document.querySelectorAll('[data-market-tab]')];
  function activate(name,focus=false){
    if(!tabs.some(t=>t.dataset.marketTab===name))name='create';
    document.body.classList.toggle('manual-desk-open',name==='manual');
    document.getElementById('manualChartCard').hidden=name!=='manual';
    if(name==='approved')document.getElementById('refreshShadow').click();
    for(const tab of tabs){const selected=tab.dataset.marketTab===name;tab.classList.toggle('active',selected);tab.setAttribute('aria-selected',String(selected));tab.tabIndex=selected?0:-1;const pane=document.getElementById(tab.getAttribute('aria-controls'));pane.hidden=!selected;if(pane.tagName==='DETAILS')pane.open=true;if(selected&&focus)tab.focus();}
  }
  tabs.forEach((tab,i)=>{tab.addEventListener('click',()=>activate(tab.dataset.marketTab));tab.addEventListener('keydown',e=>{if(!['ArrowLeft','ArrowRight','Home','End'].includes(e.key))return;e.preventDefault();const next=e.key==='Home'?0:e.key==='End'?tabs.length-1:(i+(e.key==='ArrowRight'?1:-1)+tabs.length)%tabs.length;activate(tabs[next].dataset.marketTab,true);});});
  document.querySelectorAll('[role=tabpanel]>summary').forEach(summary=>summary.addEventListener('click',e=>e.preventDefault()));
  window.PhoenixMarketNavigate=activate;
  activate(new URLSearchParams(location.search).get('section')||'create');
})();
