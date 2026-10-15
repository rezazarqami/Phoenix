(() => {
  'use strict';
  const form=document.querySelector('#loginForm'),message=document.querySelector('#loginMessage'),password=document.querySelector('#password'),toggle=document.querySelector('#togglePassword');
  toggle.addEventListener('click',()=>{const visible=password.type==='password';password.type=visible?'text':'password';toggle.textContent=visible?'پنهان':'نمایش';toggle.setAttribute('aria-pressed',String(visible));toggle.setAttribute('aria-label',visible?'پنهان‌کردن رمز عبور':'نمایش رمز عبور');});
  form.addEventListener('submit',async event=>{
    event.preventDefault();message.textContent='';const button=form.querySelector('.submit');button.disabled=true;
    try {
      const values=Object.fromEntries(new FormData(form));
      const response=await fetch('/api/auth/login',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify(values)});
      const data=await response.json();if(!response.ok)throw new Error(response.status===429?'تلاش‌های ورود زیاد شده؛ یک دقیقه بعد دوباره امتحان کن.':data.error||'ورود ناموفق بود.');
      password.value='';location.replace(data.isAdmin?'/':'/phoenix');
    }catch(error){message.textContent=error.message;button.disabled=false;}
  });
})();
