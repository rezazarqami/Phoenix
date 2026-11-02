// Keep native select values and events; only replace the browser-owned popup.
(() => {
 'use strict';
 const styles=`
 .phoenix-batch-picker{box-sizing:border-box;position:fixed;inset:0;margin:auto;width:min(300px,calc(100% - 36px));max-height:calc(100dvh - 48px);padding:10px;border:1px solid #2372a3;border-radius:15px;background:#04172c;color:#e5f2ff;box-shadow:0 18px 65px #0009,inset 0 0 18px #087bff18;font:12px/1.6 PhoenixVazir,Tahoma,sans-serif;direction:rtl;overflow:auto}
 .phoenix-batch-picker::backdrop{background:#000b;backdrop-filter:blur(2px)}
 .phoenix-batch-picker .picker-heading{display:flex;align-items:center;justify-content:space-between;gap:10px;margin:0 0 8px;padding:0 3px 7px;border-bottom:1px solid #204060}
 .phoenix-batch-picker .picker-heading strong{font:600 13px PhoenixVazir,Tahoma,sans-serif;color:#d5eeff}
 .phoenix-batch-picker button{font:inherit;cursor:pointer}
 .phoenix-batch-picker .picker-close{display:grid;place-items:center;width:28px;height:28px;padding:0;border:1px solid #254b6d;border-radius:8px;background:#082440;color:#9ed9f7;font:20px Arial}
 .phoenix-batch-picker .picker-option{display:flex;align-items:center;gap:9px;width:100%;min-height:35px;margin:3px 0;padding:6px 9px;text-align:right;border:1px solid transparent;border-radius:8px;background:transparent;color:#c5d7ed;font-size:12px}
 .phoenix-batch-picker .picker-option[aria-selected=true]{background:#07365a;border-color:#1699d0;color:#88e5ff}
 .phoenix-batch-picker .picker-option:before{content:'';box-sizing:border-box;width:13px;height:13px;flex:0 0 13px;border:1px solid #527595;border-radius:50%}
 .phoenix-batch-picker .picker-option[aria-selected=true]:before{border:4px solid #34c9f6;background:#04172c}
 .phoenix-batch-picker button:focus-visible{outline:2px solid #54cfff;outline-offset:1px}
 #market-pane-create select[data-custom-picker]{display:none!important}
 #market-pane-create .batch-picker-trigger{display:flex;align-items:center;justify-content:space-between;gap:7px;min-width:0;width:100%;height:27px;padding:3px 7px;border:1px solid #2e5276;border-radius:8px;background:#03182b;color:#e7f1ff;font:500 12px PhoenixVazir,Tahoma,sans-serif;direction:rtl;text-align:right;cursor:pointer}
 #market-pane-create .batch-picker-trigger span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
 #market-pane-create .batch-picker-trigger:after{content:'';width:6px;height:6px;flex:0 0 6px;border-right:1.5px solid #a7c6e4;border-bottom:1.5px solid #a7c6e4;transform:rotate(45deg);margin:0 3px 3px}
 #market-pane-create .batch-picker-trigger:disabled{opacity:.5;cursor:default}
 @media(min-width:700px){#market-pane-create .batch-picker-trigger{height:32px;font-size:13px}}
 `;
 function installStyle(doc){if(doc.getElementById('phoenix-batch-picker-style'))return;const style=doc.createElement('style');style.id='phoenix-batch-picker-style';style.textContent=styles;doc.head.append(style);}
 installStyle(document);
 let activeClose=null;
 for(const select of document.querySelectorAll('#market-pane-create select')){
  if(select.dataset.customPicker)continue;
  const label=select.closest('label'),caption=label?.querySelector('.field-label')?.textContent||label?.firstChild?.textContent?.trim()||'انتخاب گزینه';
  const trigger=document.createElement('button'),value=document.createElement('span');trigger.type='button';trigger.className='batch-picker-trigger';trigger.setAttribute('aria-label',caption);trigger.setAttribute('aria-haspopup','dialog');trigger.setAttribute('aria-expanded','false');trigger.append(value);
  select.dataset.customPicker='true';select.hidden=true;select.tabIndex=-1;select.setAttribute('aria-hidden','true');select.after(trigger);
  function sync(){value.textContent=select.selectedOptions[0]?.textContent||'انتخاب کنید';trigger.disabled=select.disabled;if(select.disabled&&trigger.getAttribute('aria-expanded')==='true')activeClose?.();}
  select.addEventListener('change',sync);new MutationObserver(sync).observe(select,{attributes:true,childList:true,subtree:true});sync();
  trigger.addEventListener('click',()=>{
   if(select.disabled)return;activeClose?.();
   let doc=document;try{if(parent!==window&&parent.location.origin===location.origin)doc=parent.document;}catch{}
   installStyle(doc);
   const dialog=doc.createElement('dialog');dialog.className='phoenix-batch-picker';dialog.setAttribute('aria-label',caption);
   const heading=doc.createElement('div');heading.className='picker-heading';const title=doc.createElement('strong');title.textContent=caption;
   const close=doc.createElement('button');close.type='button';close.className='picker-close';close.textContent='×';close.setAttribute('aria-label','بستن');heading.append(title,close);dialog.append(heading);
   const list=doc.createElement('div');list.setAttribute('role','listbox');list.setAttribute('aria-label',caption);dialog.append(list);
   let settled=false;
   function finish(){if(settled)return;settled=true;dialog.close();dialog.remove();trigger.setAttribute('aria-expanded','false');activeClose=null;trigger.focus();}
   activeClose=finish;
   const buttons=[];
   for(const option of select.options){if(option.disabled||option.hidden)continue;const button=doc.createElement('button');button.type='button';button.className='picker-option';button.setAttribute('role','option');button.setAttribute('aria-selected',String(option.selected));button.textContent=option.textContent;button.tabIndex=option.selected?0:-1;
    button.addEventListener('click',()=>{if(select.disabled){finish();return;}const changed=select.value!==option.value;select.value=option.value;if(changed){select.dispatchEvent(new Event('input',{bubbles:true}));select.dispatchEvent(new Event('change',{bubbles:true}));}sync();finish();});list.append(button);buttons.push(button);}
   list.addEventListener('keydown',event=>{if(!['ArrowDown','ArrowUp','Home','End'].includes(event.key))return;event.preventDefault();const index=buttons.indexOf(doc.activeElement),next=event.key==='Home'?0:event.key==='End'?buttons.length-1:(index+(event.key==='ArrowDown'?1:-1)+buttons.length)%buttons.length;buttons.forEach((b,i)=>b.tabIndex=i===next?0:-1);buttons[next]?.focus();});
   close.addEventListener('click',finish);dialog.addEventListener('cancel',e=>{e.preventDefault();finish();});dialog.addEventListener('close',finish);dialog.addEventListener('click',e=>{if(e.target!==dialog)return;const r=dialog.getBoundingClientRect();if(e.clientX<r.left||e.clientX>r.right||e.clientY<r.top||e.clientY>r.bottom)finish();});
   doc.body.append(dialog);trigger.setAttribute('aria-expanded','true');dialog.showModal();(buttons.find(b=>b.getAttribute('aria-selected')==='true')||buttons[0]||close).focus();
  });
 }
})();
