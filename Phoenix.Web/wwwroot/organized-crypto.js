(() => {
 if(window===top)return;
 document.body.classList.add('organized-crypto');
 const style=document.createElement('style');style.textContent='.organized-crypto .market-tabs,.organized-crypto .market-stats{display:none!important}';document.head.append(style);
 window.addEventListener('message',e=>{if(e.origin!==location.origin||e.source!==parent||e.data?.type!=='phoenix-workspace')return;window.PhoenixMarketNavigate?.(e.data.section);});
 document.addEventListener('click',e=>{const b=e.target.closest('#marketActiveCard');if(!b)return;e.preventDefault();e.stopImmediatePropagation();parent.postMessage({type:'phoenix-navigate',panel:'tracking',query:'view=pending'},location.origin);},true);
 parent.postMessage({type:'phoenix-workspace-ready'},location.origin);
})();
