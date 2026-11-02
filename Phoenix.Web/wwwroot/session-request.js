// Coalesce only identity reads in this document. Every protected operation remains server-authorized.
(() => {
 'use strict';let shared=null,pending=false,expires=0;
 function request(){
  if(shared&&(pending||Date.now()<expires))return shared.then(r=>new Response(r.body,{status:r.status,statusText:r.statusText,headers:r.headers}));
  pending=true;const controller=new AbortController(),timer=setTimeout(()=>controller.abort(),12000);
  shared=fetch('/api/auth/me',{cache:'no-store',credentials:'same-origin',signal:controller.signal})
   .then(async response=>{const body=await response.text();if(!response.ok)shared=null;expires=Date.now()+5000;return {body:body||null,status:response.status,statusText:response.statusText,headers:response.headers};})
   .catch(error=>{shared=null;throw error;}).finally(()=>{clearTimeout(timer);pending=false;});
  return shared.then(r=>new Response(r.body,{status:r.status,statusText:r.statusText,headers:r.headers}));
 }
 request.clear=()=>{if(!pending){shared=null;expires=0;}};
 window.PhoenixSessionRequest=request;
 window.addEventListener('phoenix-native-resume',request.clear);
 document.addEventListener('visibilitychange',()=>{if(!document.hidden)request.clear();});
})();
