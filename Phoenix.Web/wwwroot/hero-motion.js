(() => {
 'use strict';
 const frame=document.querySelector('.phoenix-hero-animation');if(!frame)return;
 let visible=true;
 // The decorative sandbox has an opaque origin; the message only controls animation visibility.
 const publish=()=>frame.contentWindow?.postMessage({type:'phoenix-hero-visibility',visible:visible&&!document.hidden},'*');
 frame.addEventListener('load',publish);document.addEventListener('visibilitychange',publish);
 if('IntersectionObserver' in window)new IntersectionObserver(entries=>{visible=entries[0].isIntersecting;publish();}).observe(frame);
 publish();
})();
