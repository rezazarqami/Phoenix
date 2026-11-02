// Market-only notices. No strategy, position, cancellation or execution requests.
(() => {
 'use strict';let busy=false,lastShown='';const toast=document.getElementById('riskToast'),badge=document.getElementById('riskBadge');
 document.getElementById('riskToastClose').addEventListener('click',()=>{toast.hidden=true;});
 toast.querySelector('[data-destination]').addEventListener('click',()=>{toast.hidden=true;});
 async function poll(){if(busy||document.hidden)return;busy=true;try{const r=await fetch('/api/market-risk',{cache:'no-store',signal:AbortSignal.timeout(10000)});if(!r.ok)return;const data=await r.json();let read=0;try{read=Number(sessionStorage.getItem('phoenix-risk-read')||0);}catch{}
 const recent=data.alerts.filter(a=>new Date(a.at).getTime()>Math.max(read,Date.now()-3600000));badge.hidden=!recent.length;
 const alert=recent.find(a=>a.severity==='danger'&&Date.now()-new Date(a.at).getTime()<300000);if(alert&&alert.id!==lastShown){lastShown=alert.id;document.getElementById('riskToastTitle').textContent='رادار ریسک · '+alert.title;document.getElementById('riskToastDetail').textContent=alert.detail;toast.hidden=false;}
 if(!recent.length)toast.hidden=true;
 }catch{/* Market data failure must not affect any other page. */}finally{busy=false;}}
 window.addEventListener('phoenix-session-ready',poll);document.addEventListener('visibilitychange',()=>{if(!document.hidden)poll();});poll();setInterval(poll,30000);
})();
