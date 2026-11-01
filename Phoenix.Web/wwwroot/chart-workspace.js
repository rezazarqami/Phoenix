/* Dedicated chart workspace, including the embedded mobile shell. */
(() => {
 'use strict';
 const $=id=>document.getElementById(id),editor=$('manualEditor');if(!editor)return;
 const card=editor.querySelector('article'),tools=card.querySelector('.manual-tools'),controls=card.querySelector('.manual-controls');
 let view='assets',visible=false,focused=false,returnScroll=0;
 const button=(label,html)=>{const b=document.createElement('button');b.type='button';b.setAttribute('aria-label',label);b.title=label;b.innerHTML=html;return b;};
 const icon=d=>`<svg viewBox="0 0 24 24" aria-hidden="true"><path d="${d}"/></svg>`;
 const back=button('بازگشت به نمادهای قابل معامله',icon('m14 5-7 7 7 7'));back.className='workspace-back';
 const symbol=$('manualSymbol');symbol.tabIndex=0;symbol.setAttribute('role','button');symbol.setAttribute('aria-label','انتخاب نماد');
 symbol.addEventListener('click',()=>back.click());symbol.addEventListener('keydown',e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();back.click();}});
 const interval=$('manualInterval'),wheel=document.createElement('div');wheel.className='workspace-interval';
 const labels=['1m','5m','15m','30m','1h','4h','1D','1W','1M','3M'];
 const prev=button('تایم‌فریم قبلی',''),next=button('تایم‌فریم بعدی','');
 const current=document.createElement('label');current.className='workspace-current';const caption=document.createElement('span');
 interval.parentElement.replaceWith(wheel);current.append(caption,interval);wheel.append(prev,current,next);
 function updateInterval(){const i=interval.selectedIndex;caption.textContent=labels[i];prev.textContent=labels[i-1]||'';next.textContent=labels[i+1]||'';prev.disabled=i===0;next.disabled=i===labels.length-1;}
 function step(delta){interval.selectedIndex+=delta;interval.dispatchEvent(new Event('change'));}
 prev.addEventListener('click',()=>step(-1));next.addEventListener('click',()=>step(1));interval.addEventListener('change',updateInterval);updateInterval();
 const palette=document.createElement('dialog');palette.className='manual-confirm workspace-palette';
 const heading=document.createElement('header'),title=document.createElement('strong'),close=button('بستن ابزارها','×');heading.append(title,close);close.addEventListener('click',()=>palette.close());
 const list=$('manualToolList');palette.append(heading,list);card.append(palette);
 const actions=document.createElement('div');actions.className='workspace-drawing-actions';
 for(const id of ['manualUndo','manualDrawingEdit','manualDrawingDelete','manualDrawingClear'])actions.append($(id));palette.append(actions);
 const groups=[['drawing','ابزارهای رسم','m4 20 16-16M4 20l5-1-4-4Z'],['fib','ابزارهای فیبوناچی','M3 4h18M7 9h14M10 14h11M3 20h18'],['elliott','ابزارهای الیوت','m2 20 4-10 4 6 5-13 4 7 3-5']];
 const groupButtons=groups.map(([group,label,path])=>{const b=button(label,icon(path));b.dataset.group=group;b.addEventListener('click',()=>{title.textContent=label;list.querySelectorAll('button').forEach(item=>item.hidden=item.dataset.toolGroup!==group);palette.showModal();});return b;});
 const cursor=tools.querySelector('[data-manual-tool=cursor]');actions.prepend(cursor);
 $('manualResetView').hidden=true;$('manualLarge').hidden=true;
 tools.prepend(back,symbol,wheel,...groupButtons);
 controls.querySelector('.manual-symbol').hidden=true;
 for(const label of controls.querySelectorAll(':scope > label')){const text=label.firstChild;text.textContent=text.textContent.replace(' (USDT)','');}
 const levelLabel=document.createElement('label');levelLabel.className='workspace-level-toggle';
 const levelCheck=document.createElement('input');levelCheck.type='checkbox';levelCheck.id='manualShowLevels';levelCheck.checked=true;levelCheck.setAttribute('aria-label','نمایش خطوط سیگنال');
 const levelText=document.createElement('span');levelText.textContent='خطوط';levelLabel.append(levelText,levelCheck);controls.append(levelLabel);
 card.prepend($('manualChart'));card.append(tools,controls);
 const review=$('manualReview');review.title=review.textContent;review.textContent='✓';review.setAttribute('aria-label',review.title);tools.append(review);
 const status=$('manualStatus');status.classList.add('workspace-status');card.append(status);
 function sync(active){visible=active;const on=visible&&view==='chart';if(on===focused)return;focused=on;
  document.documentElement.classList.toggle('chart-workspace-open',on);document.body.classList.toggle('chart-workspace-open',on);
  if(on){returnScroll=window.scrollY;history.pushState({...history.state,phoenixChart:true},'');}else{palette.close();window.scrollTo(0,returnScroll);}
  if(window.parent!==window)window.parent.postMessage({type:'phoenix-chart-focus',active:on},location.origin);
  window.dispatchEvent(new Event('resize'));
 }
 function leave(){window.dispatchEvent(new Event('phoenix-chart-back'));}
 back.addEventListener('click',()=>{if(history.state?.phoenixChart)history.back();else leave();});
 window.addEventListener('popstate',()=>{if(focused)leave();});
 window.PhoenixChartWorkspace={setView(next,active){view=next;sync(active);},sync};
})();
