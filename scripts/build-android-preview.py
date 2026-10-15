from pathlib import Path
import re, base64
root=Path(__file__).resolve().parents[1]
p=root/'Phoenix.Web/wwwroot'
html=(p/'index.html').read_text().split('<body data-personal-workspace="2">')[1].split('</body>')[0]
html=re.sub(r'<script[^>]*>.*?</script>','',html,flags=re.S)
html=html.replace('href="/phoenix"','href="https://marketsignalist.ir/phoenix"').replace('href="/"','href="https://marketsignalist.ir/"')
css=(p/'personal.css').read_text()
css=css.replace('/fonts/Vazirmatn-variable.woff2', 'data:font/woff2;base64,'+base64.b64encode((p/'fonts/Vazirmatn-variable.woff2').read_bytes()).decode())
js=(p/'personal.js').read_text().replace('fetch(', 'previewUnavailable(')
start=js.index('      if ("serviceWorker" in navigator) {')
end=js.index('      await synchronize();',start)
js=js[:start]+js[end:]
js=js.replace('"همگام‌شده"','"نسخهٔ نمایشی"').replace('ثبت روی این گوشی ذخیره شد.', 'در پیش‌نمایش ثبت شد؛ با بستن نمایش پاک می‌شود.')
core=(p/'personal-core.js').read_text()
mock='''
function previewUnavailable(){throw Error('این قابلیت به سرور نیاز دارد و در پیش‌نمایش فعال نیست.');}
window.PersonalStorage=(()=>{
 const records=[],files=[];const today=PersonalCore.iso();
 function seed(kind,title,details={},extra={}){records.push({id:crypto.randomUUID(),kind,title,date:today,details:{status:'active',attachmentIds:[],...details},createdAt:new Date().toISOString(),revision:1,notes:'',...extra});}
 seed('task','بررسی صدای ساز جدید',{status:'todo',important:true,dueTime:'10:00'});
 seed('task','ثبت هزینه‌های امروز',{status:'todo',important:true,dueTime:'18:00'});
 seed('goal','تکمیل نمونهٔ جدید هنگ‌درام',{expectedOutcome:'رسیدن به صدای متعادل در همهٔ نت‌ها'});
 seed('project','آزمایش تیون نمونهٔ جدید',{expectedOutcome:'ثبت نتیجهٔ هر مرحله'});
 seed('habit','۲۰ دقیقه پیاده‌روی',{cadence:'week',target:4});
 seed('checkin','وضعیت امروز',{energy:4,focus:3,mood:4,sleepHours:7,activities:['کارگاه','پیاده‌روی']});
 seed('time','تمرین کوک',{durationMinutes:45,status:'done'});
 seed('task','مرور ایده‌های تازه',{status:'done',important:true});
 seed('journal','ایده‌های امروز',{}, {notes:'این اطلاعات نمونه‌اند؛ برای دیدن بخش‌های برنامه استفاده شده‌اند.'});
 seed('finance','خرید ابزار',{}, {amount:850000,flow:'expense',category:'کارگاه'});
 seed('decision','مقایسهٔ دو روش تیون',{hypothesis:'مقایسهٔ ثبات صدا پس از استراحت',expectedOutcome:'انتخاب روش پایدارتر',reviewDate:today});
 seed('craft','ثبت تجربهٔ پرداخت سطح',{materials:'ورق آزمایشی',method:'پرداخت مرحله‌ای',observation:'سطح یکنواخت‌تر',conclusion:'نیاز به تکرار آزمایش'});
 return {ApiError:Error,identity:async()=>({username:'preview',isAdmin:true}),read:async(store,id)=>store==='records'?records.slice():store==='files'?files.slice():store==='meta'?{key:'owner',value:'preview'}:[],input:e=>({...e,clientId:e.id}),save:async(v,e)=>{let row={...v,id:e?.id||crypto.randomUUID(),revision:(e?.revision||0)+1,createdAt:e?.createdAt||new Date().toISOString()};let i=records.findIndex(x=>x.id===row.id);if(i<0)records.push(row);else records[i]=row;return row;},remove:async(e)=>{let i=records.findIndex(x=>x.id===e.id);if(i>=0)records.splice(i,1);},sync:async(cb)=>{if(cb)await cb();},clear:async()=>{},write:async()=>{},resolve:async()=>{},api:async(path)=>{if(path==='trash'||path.endsWith('/history'))return [];throw Error('این بخش به سرور نیاز دارد.');},addFile:async()=>{throw Error('فایل در پیش‌نمایش بارگذاری نمی‌شود.');},fileUrl:async()=>({})};
})();
'''
out=root/'android/app/src/main/assets/preview/index.html'
out.parent.mkdir(parents=True,exist_ok=True)
mobile=(p/'mobile.js').read_text()
banner='<aside style="padding:13px 21px;background:#dcfa52;color:#261c30;font:13px Tahoma;text-align:center">پیش‌نمایش شاخه · ثبت‌ها نمونه و موقت‌اند؛ اتصال به سرور غیرفعال است.</aside>'
out.write_text('<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover"><meta name="theme-color" content="#100d1a"><title>پیش‌نمایش مدار</title><style>'+css+'</style></head><body data-personal-workspace="2">'+banner+html+'<script>window.OrbitPreview=true;'+core+'\n'+mock+'\n'+mobile+'\n'+js+'</script></body></html>')
print('Built bundled branch preview:',out.name,out.stat().st_size,'bytes')
