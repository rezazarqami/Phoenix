package ir.marketsignalist.phoenix;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.Uri;
import android.os.*;
import android.provider.Settings;
import android.view.*;
import android.webkit.*;
import android.widget.*;
import androidx.annotation.NonNull;
import androidx.webkit.*;
import com.google.firebase.FirebaseApp;
import com.google.firebase.messaging.FirebaseMessaging;
import org.json.*;
import java.util.Collections;
import java.util.UUID;

public final class MainActivity extends Activity {
    private static final String HOME="https://marketsignalist.ir";
    private static final String START_ROUTE=BuildConfig.PHOENIX_ONLY?"/phoenix":"/";
    private WebView browser;
    private FrameLayout root;
    private TextView pageStatus;
    private LinearLayout pageCover;
    private Button retry, connectionCheck;
    private int loadProgress=0;
    private String loadStage="شروع درخواست";
    private boolean checkingConnection=false;
    private final java.util.concurrent.ExecutorService network=java.util.concurrent.Executors.newSingleThreadExecutor();
    private final Handler ui=new Handler(Looper.getMainLooper());
    private boolean pageLoading=false, pageFailed=false;
    private long navigation=0;
    private String lastUrl=HOME+START_ROUTE;
    private final Runnable loadTimeout=()->{if(pageLoading){pageError("باز شدن صفحه کامل نشد. مرحله: "+loadStage+"؛ پیشرفت: "+loadProgress+"٪. در حال بررسی مستقل اتصال گوشی…");checkConnection();}};
    private boolean unlocked=true, initialLoaded=false;
    private String requestedRoute=START_ROUTE;
    private boolean openReminders=false;
    private boolean resumed=false;
    private JavaScriptReplyProxy smsReply;
    private String smsRequestId;
    private android.webkit.ValueCallback<Uri[]> files;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_SECURE);
        getWindow().setStatusBarColor(Color.rgb(245,243,248));getWindow().setNavigationBarColor(Color.WHITE);
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        root=new FrameLayout(this);root.setBackgroundColor(Color.rgb(245,243,248));
        root.setOnApplyWindowInsetsListener((view,insets)->{view.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());return insets;});
        browser=new WebView(this);browser.setBackgroundColor(Color.rgb(245,243,248));browser.setOverScrollMode(View.OVER_SCROLL_NEVER);
        root.addView(browser,new FrameLayout.LayoutParams(-1,-1));makePageCover();setContentView(root);readIntent(getIntent());
        WebSettings settings=browser.getSettings();settings.setJavaScriptEnabled(true);settings.setDomStorageEnabled(true);
        settings.setUserAgentString(settings.getUserAgentString()+" PhoenixAndroid/1.9");settings.setAllowFileAccess(false);settings.setAllowContentAccess(false);settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);settings.setJavaScriptCanOpenWindowsAutomatically(false);
        CookieManager.getInstance().setAcceptCookie(true);CookieManager.getInstance().setAcceptThirdPartyCookies(browser,false);
        browser.setWebChromeClient(new WebChromeClient(){
            @Override public void onProgressChanged(WebView view,int progress){loadProgress=progress;}
            @Override public boolean onShowFileChooser(WebView web,android.webkit.ValueCallback<Uri[]> callback,FileChooserParams params){
                if(!unlocked||!TrustedNavigation.trusted(web.getUrl()))return false;
                if(files!=null)files.onReceiveValue(null);files=callback;
                Intent choose=new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").putExtra(Intent.EXTRA_MIME_TYPES,new String[]{"image/jpeg","image/png","image/webp","audio/*"}).putExtra(Intent.EXTRA_ALLOW_MULTIPLE,true);
                try{startActivityForResult(choose,43);}catch(ActivityNotFoundException e){files.onReceiveValue(null);files=null;}return true;
            }
        });
        browser.setWebViewClient(new WebViewClient(){
            @Override public WebResourceResponse shouldInterceptRequest(WebView view,WebResourceRequest request){
                if(TrustedNavigation.trusted(request.getUrl().toString())&&"POST".equals(request.getMethod())&&(request.getUrl().getPath().equals("/api/auth/logout")||request.getUrl().getPath().equals("/api/analysis/auth/logout")))runOnUiThread(()->{try{resetDevice();}catch(Exception ignored){}});
                return null;
            }
            @Override public void onPageStarted(WebView view,String url,android.graphics.Bitmap icon){
                if(TrustedNavigation.trusted(url)){lastUrl=url;beginLoading();loadStage="دریافت صفحه";}
            }
            @Override public void onPageCommitVisible(WebView view,String url){pageReady(url);}
            @Override public void onPageFinished(WebView view,String url){
                CookieManager.getInstance().flush();pageReady(url);
            }
            @Override public void onReceivedError(WebView view,WebResourceRequest request,WebResourceError error){
                if(request.isForMainFrame())pageError("اتصال به سرور برقرار نشد ("+error.getErrorCode()+"). اتصال اینترنت را بررسی کن.");
            }
            @Override public void onReceivedHttpError(WebView view,WebResourceRequest request,WebResourceResponse response){
                if(request.isForMainFrame())pageError("سرور پاسخ ناموفق داد (HTTP "+response.getStatusCode()+"). دوباره تلاش کن.");
            }
            @Override public void onReceivedSslError(WebView view,SslErrorHandler handler,android.net.http.SslError error){
                handler.cancel();pageError("اتصال امن سرور تأیید نشد. تاریخ و ساعت گوشی و اتصال اینترنت را بررسی کن.");
            }
            @Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest request){
                String url=request.getUrl().toString();if(TrustedNavigation.trusted(url))return false;
                if(request.isForMainFrame() && ("https".equals(request.getUrl().getScheme())||"http".equals(request.getUrl().getScheme())))try{startActivity(new Intent(Intent.ACTION_VIEW,request.getUrl()));}catch(ActivityNotFoundException ignored){}
                return true;
            }
        });
        if(WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)){
            WebViewCompat.addWebMessageListener(browser,"OrbitNative",Collections.singleton(HOME),(view,message,origin,mainFrame,reply)->{
                if(!unlocked||!mainFrame||!TrustedNavigation.trusted(origin.toString())||!TrustedNavigation.trusted(view.getUrl()))return;
                handleMessage(message.getData(),reply);
            });
        }
        unlock();
    }
    private int dp(int n){return (int)(n*getResources().getDisplayMetrics().density);}
    private void makePageCover(){
        pageCover=new LinearLayout(this);pageCover.setOrientation(LinearLayout.VERTICAL);pageCover.setGravity(Gravity.CENTER);pageCover.setPadding(dp(34),dp(34),dp(34),dp(34));pageCover.setBackgroundColor(Color.rgb(35,26,47));
        TextView title=new TextView(this);title.setText("مدار و فونیکس");title.setTextColor(Color.WHITE);title.setTextSize(30);pageCover.addView(title);
        pageStatus=new TextView(this);pageStatus.setTextColor(Color.rgb(230,215,240));pageStatus.setTextSize(16);pageStatus.setGravity(Gravity.CENTER);pageStatus.setPadding(0,dp(21),0,dp(21));pageCover.addView(pageStatus);
        retry=new Button(this);retry.setText("تلاش مجدد");retry.setBackgroundTintList(android.content.res.ColorStateList.valueOf(Color.rgb(220,250,82)));retry.setOnClickListener(v->{if(unlocked){browser.stopLoading();loadPage(lastUrl);}});pageCover.addView(retry);
        connectionCheck=new Button(this);connectionCheck.setText("بررسی اتصال همین گوشی");connectionCheck.setOnClickListener(v->{if(unlocked&&pageFailed)checkConnection();});pageCover.addView(connectionCheck);
        pageCover.setVisibility(View.GONE);root.addView(pageCover,new FrameLayout.LayoutParams(-1,-1));
    }
    private void renderPageState(){
        if(!unlocked){pageCover.setVisibility(View.GONE);return;}
        boolean covered=pageLoading||pageFailed;
        pageCover.setVisibility(covered?View.VISIBLE:View.GONE);
        // Keep the WebView visible behind the opaque cover so its compositor can produce a frame.
        browser.setVisibility(View.VISIBLE);
        retry.setVisibility(pageFailed?View.VISIBLE:View.GONE);connectionCheck.setVisibility(pageFailed?View.VISIBLE:View.GONE);connectionCheck.setEnabled(!checkingConnection);
    }
    private void beginLoading(){
        navigation++;loadProgress=0;loadStage="شروع درخواست";pageLoading=true;pageFailed=false;pageStatus.setText("در حال اتصال و بازکردن برنامه…");
        ui.removeCallbacks(loadTimeout);ui.postDelayed(loadTimeout,30000);renderPageState();
    }
    private void pageReady(String url){
        // A committed document is usable even when a font/image is still loading.
        // Never make page availability depend on a compositor fence beneath an opaque cover.
        if(!pageFailed&&TrustedNavigation.trusted(url)&&url.equals(browser.getUrl())){
            lastUrl=url;pageLoading=false;ui.removeCallbacks(loadTimeout);renderPageState();
        }
    }
    private void checkConnection(){
        if(checkingConnection||isDestroyed())return;
        checkingConnection=true;connectionCheck.setEnabled(false);
        final long attempt=navigation;
        final String context=pageStatus.getText().toString().split("\n")[0];
        final String agent=browser.getSettings().getUserAgentString();
        network.execute(()->{
            javax.net.ssl.HttpsURLConnection connection=null;String result;
            try{
                connection=(javax.net.ssl.HttpsURLConnection)new java.net.URL(HOME+"/login").openConnection();
                connection.setConnectTimeout(10000);connection.setReadTimeout(10000);connection.setInstanceFollowRedirects(false);connection.setUseCaches(false);
                connection.setRequestProperty("User-Agent",agent);
                int code=connection.getResponseCode();
                result=code==200?"آزمون همین گوشی: سرور پاسخ سالم داد (HTTP 200). دریافت یا نمایش صفحه داخل برنامه کامل نشده است.":"آزمون همین گوشی: پاسخ سرور HTTP "+code+"؛ ممکن است محدودیت دسترسی یا خطای سرور باشد.";
            }catch(java.net.UnknownHostException e){result="آزمون همین گوشی: نام دامنه پیدا نشد (DNS). اینترنت یا DNS گوشی را بررسی کن.";}
            catch(javax.net.ssl.SSLException e){result="آزمون همین گوشی: اتصال امن TLS برقرار نشد. تاریخ گوشی و گواهی سرور باید بررسی شود.";}
            catch(java.net.SocketTimeoutException e){result="آزمون همین گوشی: اتصال مستقل هم بی‌پاسخ ماند. مسیر شبکه، اپراتور یا فایروال باید بررسی شود.";}
            catch(Exception e){result="آزمون همین گوشی: اتصال برقرار نشد ("+e.getClass().getSimpleName()+").";}
            finally{if(connection!=null)connection.disconnect();}
            final String message=result;
            ui.post(()->{if(isDestroyed())return;checkingConnection=false;if(pageFailed&&attempt==navigation)pageStatus.setText(context+"\n\n"+message);renderPageState();});
        });
    }
    private void pageError(String text){pageLoading=false;pageFailed=true;pageStatus.setText(text);ui.removeCallbacks(loadTimeout);renderPageState();}
    private void loadPage(String url){lastUrl=url;beginLoading();browser.loadUrl(url);}
    private boolean deviceSecure(){return getSystemService(KeyguardManager.class).isDeviceSecure();}
    private void unlock(){
        if(isFinishing())return;
        unlocked=true;browser.setVisibility(View.VISIBLE);browser.onResume();browser.requestLayout();
        if(!initialLoaded||requestedRoute!=null){String route=requestedRoute==null?START_ROUTE:requestedRoute;loadPage(HOME+route+(openReminders&&"/".equals(route)?"?reminders=1":""));initialLoaded=true;requestedRoute=null;openReminders=false;}
        renderPageState();
        browser.evaluateJavascript("window.dispatchEvent(new Event('orbit-native-resume'))",null);
    }
    private void readIntent(Intent intent){if(intent==null)return;requestedRoute=BuildConfig.PHOENIX_ONLY?START_ROUTE:TrustedNavigation.route(intent.getStringExtra("route"));openReminders=intent.getBooleanExtra("reminders",false);}
    @Override protected void onNewIntent(Intent intent){super.onNewIntent(intent);setIntent(intent);readIntent(intent);unlock();}
    @Override protected void onResume(){super.onResume();getWindow().clearFlags(WindowManager.LayoutParams.FLAG_SECURE);resumed=true;unlock();}
    @Override protected void onPause(){resumed=false;CookieManager.getInstance().flush();browser.onPause();super.onPause();}
    @Override protected void onActivityResult(int request,int result,Intent data){
        super.onActivityResult(request,result,data);
        if(request==43&&files!=null){java.util.ArrayList<Uri> values=new java.util.ArrayList<>();if(result==RESULT_OK&&data!=null){if(data.getClipData()!=null)for(int i=0;i<data.getClipData().getItemCount();i++)values.add(data.getClipData().getItemAt(i).getUri());else if(data.getData()!=null)values.add(data.getData());}files.onReceiveValue(values.isEmpty()?null:values.toArray(new Uri[0]));files=null;}
    }
    @Override public void onRequestPermissionsResult(int request,@NonNull String[] permissions,@NonNull int[] results){
        super.onRequestPermissionsResult(request,permissions,results);
        if(request==45&&smsReply!=null){
            JavaScriptReplyProxy proxy=smsReply;String id=smsRequestId;smsReply=null;smsRequestId=null;
            readSms(proxy,id);
        }
    }
    private void readSms(JavaScriptReplyProxy proxy,String id){
        if(checkSelfPermission(Manifest.permission.READ_SMS)!=PackageManager.PERMISSION_GRANTED){try{reply(proxy,id,new JSONObject().put("granted",false).put("messages",new JSONArray()),null);}catch(JSONException ignored){}return;}
        network.execute(()->{
            JSONArray messages=new JSONArray();String error=null;
            try(android.database.Cursor cursor=getContentResolver().query(Uri.parse("content://sms/inbox"),new String[]{"_id","body","date"},"date >= ?",new String[]{String.valueOf(System.currentTimeMillis()-30L*24*60*60*1000)},"date ASC")){
                if(cursor!=null)while(cursor.moveToNext()){
                    String body=cursor.getString(1);
                    // Only debit candidates leave the native inbox reader. OTPs and other messages stay here.
                    if(body==null||body.length()>4000||!body.matches("(?s).*(برداشت|خرید|بدهکار|پرداخت|انتقال وجه).*"))continue;
                    if(body.matches("(?s).*(رمز|یکبار مصرف|واریز|بستانکار).*"))continue;
                    if(!body.matches("(?s).*(ریال|تومان).*"))continue;
                    if(!body.matches("(?s).*(بانک|حساب|کارت|مانده|موجودی).*"))continue;
                    messages.put(new JSONObject().put("id",cursor.getString(0)).put("body",body).put("at",cursor.getLong(2)));
                }
            }catch(Exception e){error="خواندن پیامک‌ها انجام نشد؛ اجازهٔ پیامک را در تنظیمات گوشی بررسی کن.";}
            final String failure=error;
            ui.post(()->{if(isDestroyed()||!TrustedNavigation.trusted(browser.getUrl())||!"/".equals(Uri.parse(browser.getUrl()).getPath()))return;try{reply(proxy,id,new JSONObject().put("granted",true).put("messages",messages),failure);}catch(JSONException ignored){}});
        });
    }
    private JSONObject status() throws JSONException {
        OrbitNotifications.channels(this);
        String id=getPreferences().getString("device-id",null);if(id==null){id=UUID.randomUUID().toString();getPreferences().edit().putString("device-id",id).apply();}
        return new JSONObject().put("reminderVersion",2).put("deviceId",id).put("deviceLock",deviceSecure()).put("notifications",OrbitNotifications.allowed(this)).put("reminderChannel",OrbitNotifications.channelAllowed(this,OrbitNotifications.REMINDERS)).put("signalChannel",OrbitNotifications.channelAllowed(this,OrbitNotifications.SIGNALS)).put("exactAlarms",Reminders.exactAllowed(this)).put("pushConfigured",!FirebaseApp.getApps(this).isEmpty()).put("enrollmentId",getPreferences().getString("push-enrollment",null)).put("pushEnabled",getPreferences().getBoolean("push-enabled",false)).put("token",getPreferences().getBoolean("push-enabled",false)?getPreferences().getString("push-token",null):JSONObject.NULL).put("reminders",Reminders.list(this));
    }
    private android.content.SharedPreferences getPreferences(){return getSharedPreferences("orbit-device",MODE_PRIVATE);}
    private void reply(JavaScriptReplyProxy proxy,String id,JSONObject value,String error){try{JSONObject out=new JSONObject().put("id",id).put("ok",error==null);if(value!=null)out.put("value",value);if(error!=null)out.put("error",error);proxy.postMessage(out.toString());}catch(Exception ignored){}}
    private void resetDevice() throws JSONException {
        getPreferences().edit().putBoolean("push-enabled",false).remove("push-token").remove("push-enrollment").apply();
        androidx.work.WorkManager.getInstance(this).cancelUniqueWork("orbit-push-registration");
        if(!FirebaseApp.getApps(this).isEmpty()){FirebaseMessaging.getInstance().setAutoInitEnabled(false);FirebaseMessaging.getInstance().deleteToken();}
        Reminders.clear(this);
    }
    private void handleMessage(String raw,JavaScriptReplyProxy proxy){
        String id="";
        try{
            if(raw==null||raw.length()>10000)throw new IllegalArgumentException("درخواست معتبر نیست.");
            JSONObject input=new JSONObject(raw);id=input.getString("id");if(id.length()>100)throw new IllegalArgumentException("شناسه معتبر نیست.");
            String command=input.getString("command");
            if(command.equals("readSms")){
                if(!"/".equals(Uri.parse(browser.getUrl()).getPath()))throw new IllegalArgumentException("پیامک فقط در فضای شخصی در دسترس است.");
                if(input.optBoolean("requestPermission")&&checkSelfPermission(Manifest.permission.READ_SMS)!=PackageManager.PERMISSION_GRANTED){
                    if(smsReply!=null)throw new IllegalStateException("درخواست اجازهٔ پیامک در انتظار است.");
                    smsReply=proxy;smsRequestId=id;requestPermissions(new String[]{Manifest.permission.READ_SMS},45);return;
                }
                readSms(proxy,id);return;
            }
            if(command.equals("configurePush")){OrbitFirebase.configure(this,input.getJSONObject("config"));reply(proxy,id,status(),null);return;}
            if(command.equals("status")){reply(proxy,id,status(),null);return;}
            if(command.equals("requestNotifications")){
                if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},44);
                else startActivity(new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,getPackageName()));
                reply(proxy,id,new JSONObject().put("requested",true),null);return;
            }
            if(command.equals("requestExactAlarms")){if(Build.VERSION.SDK_INT>=31&&!Reminders.exactAllowed(this))startActivity(new Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM,Uri.parse("package:"+getPackageName())));reply(proxy,id,new JSONObject().put("requested",true),null);return;}
            if(command.equals("schedule")){reply(proxy,id,Reminders.schedule(this,input),null);return;}
            if(command.equals("snooze")){Reminders.snooze(this,input.getString("reminderId"),input.getInt("minutes"));reply(proxy,id,status(),null);return;}
            if(command.equals("cancel")){Reminders.cancel(this,input.getString("reminderId"));reply(proxy,id,status(),null);return;}
            if(command.equals("testNotification")){if(!OrbitNotifications.channelAllowed(this,OrbitNotifications.REMINDERS))throw new IllegalStateException("اجازهٔ اعلان و کانال یادآوری را فعال کن.");OrbitNotifications.show(this,OrbitNotifications.REMINDERS,UUID.randomUUID().toString(),"/");reply(proxy,id,new JSONObject().put("shown",true),null);return;}
            if(command.equals("enablePush")){
                if(FirebaseApp.getApps(this).isEmpty())throw new IllegalStateException("تنظیم سرویس Push هنوز در این نسخه انجام نشده است.");
                if(!OrbitNotifications.channelAllowed(this,OrbitNotifications.SIGNALS))throw new IllegalStateException("اجازهٔ اعلان و کانال فونیکس را فعال کن.");
                final String requestId=id;FirebaseMessaging.getInstance().setAutoInitEnabled(true);
                FirebaseMessaging.getInstance().getToken().addOnCompleteListener(task->{if(!task.isSuccessful()){reply(proxy,requestId,null,"دریافت توکن Push انجام نشد؛ اتصال را بررسی کن.");return;}
                    try{if(!unlocked||!TrustedNavigation.trusted(browser.getUrl())){reply(proxy,requestId,null,"برنامه قفل شده؛ دوباره امتحان کن.");return;}String enrollment=getPreferences().getString("push-enrollment",null);if(enrollment==null)enrollment=UUID.randomUUID().toString();getPreferences().edit().putString("push-enrollment",enrollment).putBoolean("push-enabled",true).putString("push-token",task.getResult()).apply();reply(proxy,requestId,status().put("token",task.getResult()),null);}catch(Exception e){reply(proxy,requestId,null,"فعال‌سازی اعلان انجام نشد.");}});return;
            }
            if(command.equals("disablePush")||command.equals("logout")){
                getPreferences().edit().putBoolean("push-enabled",false).remove("push-token").remove("push-enrollment").apply();
        androidx.work.WorkManager.getInstance(this).cancelUniqueWork("orbit-push-registration");
                if(!FirebaseApp.getApps(this).isEmpty()){FirebaseMessaging.getInstance().setAutoInitEnabled(false);FirebaseMessaging.getInstance().deleteToken();}
                if(command.equals("logout"))resetDevice();
                reply(proxy,id,new JSONObject().put("disabled",true),null);return;
            }

            throw new IllegalArgumentException("این فرمان پشتیبانی نمی‌شود.");
        }catch(Exception e){reply(proxy,id,null,e instanceof JSONException?"ساختار درخواست معتبر نیست.":e.getMessage());}
    }
    @Override public void onBackPressed(){if(!unlocked){finish();return;}if(browser.canGoBack())browser.goBack();else super.onBackPressed();}
    @Override protected void onDestroy(){network.shutdownNow();ui.removeCallbacksAndMessages(null);if(files!=null)files.onReceiveValue(null);browser.destroy();super.onDestroy();}
}
