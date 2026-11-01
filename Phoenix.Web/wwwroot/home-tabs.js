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
        doc.documentElement.style.setProperty('--phoenix-viewport-height', `${window.innerHeight}px`);
        const height = Math.ceil(Math.max(doc.body.scrollHeight, doc.body.getBoundingClientRect().height))+2;
        if (height > 0 && Math.abs(frame.getBoundingClientRect().height - height) > 1)
          frame.style.height = `${height}px`;
      });
    };
    const initialize = () => {
      observer?.disconnect();
      try {
        const doc = frame.contentDocument;
        if (!doc?.body) return;
        observer = new doc.defaultView.ResizeObserver(measure);
        observer.observe(doc.body);
        measure();
      } catch { /* External navigation cannot be measured. */ }
    };
    frame.addEventListener('load', initialize);
    window.addEventListener('message', event => {
      if(event.origin===location.origin&&event.source===frame.contentWindow&&event.data?.type==='phoenix-frame-ready')initialize();
    });
    window.addEventListener('resize', measure);
    window.addEventListener('phoenix-tab-changed', measure);
  }

  let current={name:'home',query:''},cryptoSection='create';
  const labels={home:'خانه',crypto:'ایجاد سیگنال',tracking:'پیگیری معاملات',notifications:'رویدادها و نتایج',more:'بیشتر',elliott:'آزمایشگاه الیوت',signal:'آزمایشگاه سیگنال',strategy:'مدیریت استراتژی دو',statistics:'آمار'};
  function ensureFrame(name,query='') {
    if(!routes[name])return;
    const host=document.getElementById(name==='crypto'?'cryptoFrameHost':name==='notifications'?'journalFrameHost':'panel-'+name);
    const url=new URL(routes[name],location.origin),params=new URLSearchParams(query);
    for(const key of ['symbol','eventId','view','source','section']){const value=params.get(key);if(value&&value.length<=200)url.searchParams.set(key,value);}
    let frame=host.querySelector('iframe');
    if(!frame){frame=document.createElement('iframe');frame.title=labels[name];frame.className='home-content-frame';frame.setAttribute('scrolling','no');fitFrame(frame);host.append(frame);}
    if(!frame.dataset.route||(name!=='crypto'&&frame.dataset.route!==url.href)){frame.dataset.route=url.href;frame.src=url.href;}
    if(name==='crypto'){cryptoSection=params.get('section')||'create';frame.contentWindow?.postMessage({type:'phoenix-workspace',section:cryptoSection},location.origin);}
  }
  function activate(name, updateHash=true, query='') {
    if(!labels[name])name='home';
    if(name==='crypto'&&document.body.classList.contains('viewer-only')&&!['history','archive','statistics','approved','search'].includes(new URLSearchParams(query).get('section'))){name='tracking';query='view=pending';}
    const params=new URLSearchParams(query),section=params.get('section')||'create';
    const category=name==='statistics'?'notifications':['elliott','signal','strategy'].includes(name)?'more':name==='crypto'&&['approved','history','archive','search'].includes(section)?'more':name==='crypto'&&section==='statistics'?'notifications':name;
    current={name,query};
    for(const tab of tabs){const selected=tab.dataset.panel===category;tab.classList.toggle('active',selected);tab.setAttribute('aria-selected',String(selected));tab.tabIndex=selected?0:-1;}
    const target=name==='statistics'?'notifications':name;
    document.querySelectorAll('.home-section').forEach(panel=>panel.hidden=panel.id!=='panel-'+target&&!(name==='home'&&panel.id==='panel-tracking'));
    const statistics=name==='statistics';
    document.getElementById('panel-statistics').hidden=!statistics;
    document.getElementById('journalFrameHost').hidden=statistics;
    document.getElementById('resultsToolbar').hidden=category!=='notifications';
    document.querySelectorAll('[data-result-view]').forEach(b=>b.setAttribute('aria-selected',String((b.dataset.resultView==='statistics')===(statistics||section==='statistics'))));
    const form=name==='crypto'&&section==='manual'&&params.get('mode')==='form';
    document.getElementById('manualFormHost').hidden=!form;
    document.getElementById('signal-pane-new').hidden=!form;
    document.getElementById('cryptoFrameHost').hidden=form;
    document.getElementById('createTabs').hidden=category!=='crypto';
    document.getElementById('manualModes').hidden=!(category==='crypto'&&section==='manual');
    document.querySelectorAll('[data-create-section]').forEach(b=>b.setAttribute('aria-selected',String(b.dataset.createSection===section)));
    document.querySelectorAll('[data-manual-mode]').forEach(b=>b.setAttribute('aria-selected',String(b.dataset.manualMode===(form?'form':'chart'))));
    const view=name==='home'||params.get('view')==='positions'?'positions':'pending';
    document.getElementById('signal-pane-active').hidden=!['home','tracking'].includes(name)||view!=='pending';
    document.getElementById('signal-pane-positions').hidden=!['home','tracking'].includes(name)||view!=='positions';
    for(const tab of document.querySelectorAll('[data-signal-tab]')){const selected=['home','tracking'].includes(name)&&tab.dataset.signalTab===(view==='positions'?'positions':'active');tab.classList.toggle('active',selected);tab.setAttribute('aria-selected',String(selected));}
    document.querySelectorAll('[data-track-view]').forEach(b=>b.setAttribute('aria-selected',String(b.dataset.trackView===view)));
    const nested=['elliott','signal','strategy'].includes(name)||category==='more'&&name==='crypto'||section==='statistics';
    document.getElementById('workspaceBreadcrumb').hidden=!nested;
    const sectionLabels={search:'مرور و جستجوی بازار',approved:'پیگیری بدون ارسال سفارش',history:'خروجی داده‌های معاملات',archive:'آرشیو میز تأیید',statistics:'گزارش تفصیلی معاملات'};
    document.getElementById('workspaceLocation').textContent=sectionLabels[section]||labels[name];
    document.querySelector('#createHeading h1').textContent=sectionLabels[section]||'ایجاد سیگنال';
    document.querySelector('#createHeading>p:last-child').hidden=category!=='crypto';
    if(!form&&!statistics)ensureFrame(name,query);
    document.querySelector('#panel-'+target+' iframe')?.contentWindow?.postMessage({type:'phoenix-frame-visible'},location.origin);
    document.body.classList.remove('review-focus-host','chart-focus-host');
    document.body.classList.toggle('crypto-market-open',name==='crypto');
    const hash='#'+name+(query?'?'+query:'');if(updateHash&&location.hash!==hash)history.pushState(null,'',hash);
    window.dispatchEvent(new CustomEvent('phoenix-tab-changed',{detail:name}));
    if(updateHash)window.scrollTo({top:0,behavior:'instant'});
    if(name==='tracking'||name==='home')window.dispatchEvent(new CustomEvent('phoenix-signal-tab-changed',{detail:view==='positions'?'positions':'active'}));
  }
  window.PhoenixNavigate=activate;
  window.addEventListener('phoenix-session-ready',()=>{if(current.name==='crypto')activate(current.name,false,current.query);});
  function fromHash(){const [name,query='']=location.hash.slice(1).split('?');activate(name,false,query);}
  document.addEventListener('click',e=>{
    const b=e.target.closest('[data-destination],[data-create-section],[data-manual-mode],[data-track-view],[data-result-view]');if(!b)return;
    let destination=b.dataset.destination;
    if(b.dataset.createSection)destination='crypto?section='+b.dataset.createSection;
    if(b.dataset.manualMode)destination='crypto?section=manual&mode='+b.dataset.manualMode;
    if(b.dataset.trackView)destination='tracking?view='+b.dataset.trackView;
    if(b.dataset.resultView)destination=b.dataset.resultView==='statistics'?'statistics':'notifications';
    const [name,query='']=destination.split('?');activate(name,true,query);
  });
  document.getElementById('workspaceBack').addEventListener('click',()=>activate(current.query.includes('statistics')?'statistics':'more'));
  window.addEventListener('message',e=>{
    const frame=document.querySelector('#cryptoFrameHost iframe');
    if(e.origin===location.origin&&e.source===frame?.contentWindow&&['phoenix-workspace-ready','phoenix-frame-ready'].includes(e.data?.type)){
      frame.contentWindow.postMessage({type:'phoenix-workspace',section:cryptoSection},location.origin);
    }
  });
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
    if(event.origin!==location.origin||event.data?.type!=='phoenix-chart-focus')return;
    const frame=document.querySelector('#panel-crypto iframe');if(frame?.contentWindow!==event.source)return;
    document.body.classList.toggle('chart-focus-host',event.data.active===true);
  });
  window.addEventListener('message',event=>{
    if(event.origin!==location.origin||event.data?.type!=='phoenix-navigate')return;
    if(![...document.querySelectorAll('.home-content-frame')].some(frame=>frame.contentWindow===event.source))return;
    if(!labels[event.data.panel])return;
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
    if(![...document.querySelectorAll('.home-content-frame')].some(frame=>frame.contentWindow===event.source))return;
    const symbol = String(event.data.symbol || '').toUpperCase();
    if (!/^[A-Z0-9]{2,24}$/.test(symbol)) return;
    activate('signal',true,new URLSearchParams({symbol}).toString());
  });
  for(const b of document.querySelectorAll('#legacyNavigation [data-panel]'))b.addEventListener('click',()=>activate(b.dataset.panel));
  fromHash();

  document.getElementById('returnHomeButton').addEventListener('click', () => activate('home'));

  settingsToggle.addEventListener('click',()=>activate('more'));
})();
