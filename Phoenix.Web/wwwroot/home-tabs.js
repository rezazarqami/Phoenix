(() => {
  const routes = {
    crypto: '/analysis/coins?v=20260902-1&embed=1',
    signal: '/analysis/signals?v=20260827-4&embed=1',
    elliott: '/analysis?v=20260827-4&embed=1',
    strategy: '/strategy2.html?embed=1',
    notifications: '/notifications?embed=1'
  };
  const tabs = [...document.querySelectorAll('.home-tab')];
  const settingsToggle = document.querySelector('#settingsToggle');
  const settingsMenu = document.querySelector('#settingsMenu');

  function fitFrame(frame) {
    let observer;
    let queued = false;
    const measure = () => {
      if (queued) return;
      queued = true;
      setTimeout(() => {
        queued = false;
        const doc = frame.contentDocument;
        if (!doc?.body || !frame.getClientRects().length) return;
        const height = Math.ceil(Math.max(doc.body.scrollHeight, doc.body.getBoundingClientRect().height))+2;
        if (height > 0 && Math.abs(frame.getBoundingClientRect().height - height) > 1)
          frame.style.height = `${height}px`;
      });
    };
    frame.addEventListener('load', () => {
      observer?.disconnect();
      try {
        const doc = frame.contentDocument;
        if (!doc?.body) return;
        observer = new doc.defaultView.ResizeObserver(measure);
        observer.observe(doc.body);
        measure();
      } catch { /* External navigation cannot be measured. */ }
    });
    window.addEventListener('resize', measure);
    window.addEventListener('phoenix-tab-changed', measure);
  }

  function ensureFrame(name, query='') {
    const panel=document.getElementById('panel-'+name);if(!routes[name])return;
    const url=new URL(routes[name],location.origin),params=new URLSearchParams(query);
    for(const key of ['symbol','eventId','view']){const value=params.get(key);if(value&&value.length<=200)url.searchParams.set(key,value);}
    let frame=panel.querySelector('iframe');
    if(!frame){frame=document.createElement('iframe');frame.title=name==='notifications'?'رویدادها و نتایج':tabs.find(tab=>tab.dataset.panel===name).textContent.trim();frame.className='home-content-frame';frame.setAttribute('scrolling','no');fitFrame(frame);panel.append(frame);}
    if(!frame.dataset.route||(query&&frame.dataset.route!==url.href)){frame.dataset.route=url.href;frame.src=url.href;}
  }
  function activate(name, updateHash=true, query='') {
    if(!tabs.some(tab=>tab.dataset.panel===name)&&name!=='notifications')name='home';
    for(const tab of tabs){const selected=tab.dataset.panel===name;tab.classList.toggle('active',selected);tab.setAttribute('aria-selected',String(selected));tab.tabIndex=selected||(name==='notifications'&&tab.dataset.panel==='home')?0:-1;}
    document.querySelectorAll('.home-section').forEach(panel=>panel.hidden=panel.id!=='panel-'+name);
    ensureFrame(name,query);
    document.body.classList.remove('review-focus-host');
    document.body.classList.toggle('crypto-market-open',name==='crypto');
    document.querySelector('.market-card').hidden=name!=='home';
    const hash='#'+name+(query?'?'+query:'');if(updateHash&&location.hash!==hash)history.replaceState(null,'',hash);
    window.dispatchEvent(new CustomEvent('phoenix-tab-changed',{detail:name}));
  }
  function fromHash(){const [name,query='']=location.hash.slice(1).split('?');activate(name,false,query);}
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => activate(tab.dataset.panel));
    tab.addEventListener('keydown', event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 :
        (index + (event.key === 'ArrowLeft' ? 1 : -1) + tabs.length) % tabs.length;
      tabs[next].focus();
      activate(tabs[next].dataset.panel);
    });
  });
  window.addEventListener('message',event=>{
    if(event.origin!==location.origin||event.data?.type!=='phoenix-navigate')return;
    if(![...document.querySelectorAll('.home-content-frame')].some(frame=>frame.contentWindow===event.source))return;
    if(!routes[event.data.panel]&&event.data.panel!=='home')return;
    activate(event.data.panel,true,typeof event.data.query==='string'?event.data.query:'');
    window.scrollTo({top:0,behavior:'instant'});
  });
  document.addEventListener('click',event=>{
    const link=event.target.closest('a[href]');if(event.defaultPrevented||!link||link.getAttribute('href')?.startsWith('#')||link.target==='_blank'||link.hasAttribute('download')||event.ctrlKey||event.metaKey||event.shiftKey||event.button>0)return;
    const route=window.PhoenixShellRoute?.(link.href);if(!route)return;
    event.preventDefault();activate(route.panel,true,route.query);document.getElementById('notificationMenu').hidden=true;
    document.getElementById('notificationToggle').setAttribute('aria-expanded','false');window.scrollTo({top:0,behavior:'instant'});
  });
  window.addEventListener('hashchange',fromHash);
  window.addEventListener('message', event => {
    if (event.origin !== location.origin || event.data?.type !== 'phoenix-open-signal') return;
    const symbol = String(event.data.symbol || '').toUpperCase();
    if (!/^[A-Z0-9]{2,24}$/.test(symbol)) return;
    activate('signal',true,new URLSearchParams({symbol}).toString());
  });
  fromHash();

  document.getElementById('returnHomeButton').addEventListener('click', () => activate('home'));

  function closeSettings() {
    settingsMenu.hidden = true;
    settingsToggle.setAttribute('aria-expanded', 'false');
  }
  settingsToggle.addEventListener('click', () => {
    settingsMenu.hidden = !settingsMenu.hidden;
    settingsToggle.setAttribute('aria-expanded', String(!settingsMenu.hidden));
  });
  settingsMenu.addEventListener('click', event => {
    if (event.target.closest('button')) closeSettings();
  });
  document.addEventListener('click', event => {
    if (!event.target.closest('.header-actions')) closeSettings();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && !settingsMenu.hidden) {
      closeSettings();
      settingsToggle.focus();
    }
  });
})();
