/* All authenticated destinations share the one Phoenix header and six-tab shell. */
(() => {
 'use strict';
 const pages={'/phoenix':'home','/phoenix.html':'home','/analysis/coins':'crypto','/crypto-market.html':'crypto','/analysis/signals':'signal','/signal-lab.html':'signal','/analysis':'elliott','/analysis.html':'elliott','/strategy2.html':'strategy','/notifications':'notifications','/notifications.html':'notifications'};
 function resolve(value){
  const url=new URL(value,location.href),panel=pages[url.pathname];if(url.origin!==location.origin||!panel)return null;
  const query=new URLSearchParams();for(const key of ['symbol','eventId','view']){const value=url.searchParams.get(key);if(value&&value.length<=200)query.set(key,value);}
  return {panel,query:query.toString()};
 }
 window.PhoenixShellRoute=resolve;
 const route=resolve(location.href);
 if(route&&route.panel!=='home'&&window===top&&!new URLSearchParams(location.search).has('embed')){
  location.replace('/phoenix#'+route.panel+(route.query?'?'+route.query:''));return;
 }
 if(window!==top){
  document.documentElement.classList.add('embedded-view');
  document.addEventListener('click',event=>{
   const link=event.target.closest('a[href]');if(event.defaultPrevented||!link||link.getAttribute('href')?.startsWith('#')||link.target==='_blank'||link.hasAttribute('download')||event.ctrlKey||event.metaKey||event.shiftKey||event.button>0)return;
   const destination=resolve(link.href);if(!destination)return;
   event.preventDefault();parent.postMessage({type:'phoenix-navigate',...destination},location.origin);
  });
 }
})();
