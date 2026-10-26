(() => {
  'use strict';
  const paths = {
    long:'M5 19 19 5M8 5h11v11',menu:'M4 6h16M4 12h16M4 18h16',bell:'M18 8a6 6 0 0 0-12 0c0 7-3 7-3 9h18c0-2-3-2-3-9M10 21h4',
    chart:'M4 20V12h3v8zM11 20V7h3v13zM18 20V2h3v18z',bars:'M3 20V13h3v7zM10 20V8h3v12zM17 20V3h3v17z',
    globe:'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0ZM3 12h18M5 6h14M5 18h14M12 3c-5 5-5 13 0 18 5-5 5-13 0-18Z',
    wave:'M2 15c3-9 4 9 7 0s4-17 6-8 4 0 7 1',network:'M12 8V4m-6 15-3 0m15 0h3M12 8l-7 8m7-8 7 8M15 5a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM7 19a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM23 19a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM15 19a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM12 10v6',
    cube:'m12 2 9 5v10l-9 5-9-5V7l9-5Zm0 10v10M3 7l9 5 9-5',chevron:'m9 5 7 7-7 7',plus:'M12 5v14M5 12h14',clock:'M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0ZM12 6v6l4 2',rise:'M12 21V3M5 10l7-7 7 7',fall:'M5 5l14 14M10 19h9v-9',arrow:'M4 12h16m-7-7 7 7-7 7',sparkles:'m12 2 3 7 7 3-7 3-3 7-3-7-7-3 7-3 3-7ZM20 1v5M17.5 3.5h5'
  };
  document.querySelectorAll('[data-icon]').forEach(el => {
    const path=paths[el.dataset.icon];
    if(path)el.innerHTML=`<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="${path}"/></svg>`;
  });
  const tabs=[...document.querySelectorAll('[data-signal-tab]')];
  function showSignalTab(name,focus=false){
    if(name==='new'&&document.body.classList.contains('viewer-only'))name='active';
    for(const tab of tabs){
      const selected=tab.dataset.signalTab===name;
      tab.classList.toggle('active',selected);tab.setAttribute('aria-selected',String(selected));tab.tabIndex=selected?0:-1;
      document.getElementById(tab.getAttribute('aria-controls')).hidden=!selected;
      if(selected&&focus)tab.focus();
    }
    window.dispatchEvent(new CustomEvent('phoenix-signal-tab-changed',{detail:name}));
  }
  tabs.forEach((tab,i)=>{
    tab.addEventListener('click',()=>showSignalTab(tab.dataset.signalTab));
    tab.addEventListener('keydown',event=>{
      if(!['ArrowLeft','ArrowRight','Home','End'].includes(event.key))return;
      event.preventDefault();const next=event.key==='Home'?0:event.key==='End'?tabs.length-1:(i+(event.key==='ArrowRight'?1:-1)+tabs.length)%tabs.length;
      showSignalTab(tabs[next].dataset.signalTab,true);
    });
  });
  function applyViewerMode(){if(document.body.classList.contains('viewer-only')&&document.getElementById('signal-tab-new').getAttribute('aria-selected')==='true')showSignalTab('active');}
  new MutationObserver(applyViewerMode).observe(document.body,{attributes:true,attributeFilter:['class']});applyViewerMode();
  const notificationToggle=document.getElementById('notificationToggle'),notificationMenu=document.getElementById('notificationMenu');
  notificationToggle.addEventListener('click',()=>{notificationMenu.hidden=!notificationMenu.hidden;notificationToggle.setAttribute('aria-expanded',String(!notificationMenu.hidden));});
  document.getElementById('notificationShowActive').addEventListener('click',()=>{document.getElementById('tab-home').click();showSignalTab('active');notificationMenu.hidden=true;notificationToggle.setAttribute('aria-expanded','false');});
  function updateNotifications(){const count=document.getElementById('count').textContent;document.getElementById('notificationSummary').textContent=`${count} سیگنال در انتظار یا فعال دارید.`;notificationToggle.querySelector('i').hidden=!count.replace(/[۰٠0\s]/g,'');}
  new MutationObserver(updateNotifications).observe(document.getElementById('count'),{childList:true,subtree:true,characterData:true});updateNotifications();
  document.addEventListener('click',event=>{if(!event.target.closest('#notificationToggle,#notificationMenu')){notificationMenu.hidden=true;notificationToggle.setAttribute('aria-expanded','false');}});
  document.addEventListener('keydown',event=>{if(event.key==='Escape'){notificationMenu.hidden=true;notificationToggle.setAttribute('aria-expanded','false');}});
  let interval='60',controller=null,baseline=null,livePrice=null;
  const format=new Intl.NumberFormat('en-US',{maximumFractionDigits:2});
  function change(){
    if(!baseline||!livePrice)return;
    const amount=livePrice-baseline,percent=amount/baseline*100;
    document.getElementById('marketChangePercent').textContent=`${percent>=0?'+':''}${percent.toFixed(2)}%`;
    document.getElementById('marketChangeAmount').textContent=`${amount>=0?'+':''}${format.format(amount)}`;
    document.getElementById('marketChange').classList.toggle('negative',amount<0);
  }
  window.addEventListener('phoenix-market-price',event=>{livePrice=Number(event.detail);change();});
  async function loadChart(){
    controller?.abort();controller=new AbortController();const signal=controller.signal;
    const status=document.getElementById('marketChartStatus');status.textContent='در حال دریافت…';
    try{
      const response=await fetch(`/api/market/overview?interval=${interval}`,{cache:'no-store',signal});
      if(!response.ok)throw Error();const data=await response.json();if(signal.aborted)return;
      const values=data.closes.map(Number);if(values.length<2||values.some(v=>!Number.isFinite(v)||v<=0))throw Error();
      const low=Math.min(...values),range=Math.max(...values)-low||1;
      const points=values.map((v,i)=>`${(4+i*312/(values.length-1)).toFixed(2)},${(90-(v-low)*80/range).toFixed(2)}`);
      const path='M'+points.join(' L');document.getElementById('marketLine').setAttribute('d',path);
      document.getElementById('marketArea').setAttribute('d',`${path} L316,98 L4,98 Z`);
      baseline=values[0];livePrice=livePrice||values.at(-1);change();status.textContent='';
      document.getElementById('marketSparkline').setAttribute('aria-label',`نمودار بیت‌کوین؛ ${values.length} کندل، تایم‌فریم ${interval}`);
    }catch(error){if(error.name!=='AbortError'){status.textContent='دادهٔ نمودار در دسترس نیست';baseline=null;document.getElementById('marketLine').removeAttribute('d');document.getElementById('marketArea').removeAttribute('d');document.getElementById('marketChangePercent').textContent='—';document.getElementById('marketChangeAmount').textContent='—';}}
  }
  document.querySelectorAll('[data-market-interval]').forEach(button=>button.addEventListener('click',()=>{
    interval=button.dataset.marketInterval;document.querySelectorAll('[data-market-interval]').forEach(b=>{const active=b===button;b.classList.toggle('active',active);b.setAttribute('aria-pressed',String(active));});loadChart();
  }));
  loadChart();setInterval(()=>{if(!document.hidden)loadChart();},60000);
})();
