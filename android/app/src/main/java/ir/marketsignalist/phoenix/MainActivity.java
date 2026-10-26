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
    private static final String PHOENIX_UI="(()=>{const install=()=>{if(document.getElementById('phoenix-only-style'))return;const s=document.createElement('style');s.id='phoenix-only-style';s.textContent='a[href=\"/\"],a[href=\"/index.html\"],a[href^=\"/?\"]{display:none!important}';(document.head||document.documentElement).appendChild(s)};if(document.documentElement)install();else document.addEventListener('DOMContentLoaded',install,{once:true})})()";
    private static final String START_ROUTE="/phoenix";
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
    private boolean resumed=false;
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
        settings.setUserAgentString(settings.getUserAgentString()+" PhoenixAndroid/1.9.4");settings.setAllowFileAccess(false);settings.setAllowContentAccess(false);settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);settings.setJavaScriptCanOpenWindowsAutomatically(false);
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
        if(BuildConfig.PHOENIX_ONLY&&WebViewFeature.isFeatureSupported(WebViewFeature.DOCUMENT_START_SCRIPT))
            WebViewCompat.addDocumentStartJavaScript(browser,PHOENIX_UI,Collections.singleton(HOME));
        browser.setWebViewClient(new WebViewClient(){
            @Override public WebResourceResponse shouldInterceptRequest(WebView view,WebResourceRequest request){
                if(BuildConfig.PHOENIX_ONLY&&TrustedNavigation.trusted(request.getUrl().toString())) {
                    String path=request.getUrl().getPath();
                    try {
                        if(request.isForMainFrame()&&("/login".equals(path)||"/login.html".equals(path)))
                            return new WebResourceResponse("text/html","UTF-8",getAssets().open("phoenix-login.html"));
                        if("/__phoenix_app/logo.png".equals(path))
                            return new WebResourceResponse("image/png",null,getResources().openRawResource(ir.marketsignalist.phoenix.R.drawable.phoenix_standalone_logo));
                    } catch(java.io.IOException e) { return new WebResourceResponse("text/plain","UTF-8",new java.io.ByteArrayInputStream("Phoenix login unavailable".getBytes(java.nio.charset.StandardCharsets.UTF_8))); }
                }
                if(TrustedNavigation.trusted(request.getUrl().toString())&&"POST".equals(request.getMethod())&&(request.getUrl().getPath().equals("/api/auth/logout")||request.getUrl().getPath().equals("/api/analysis/auth/logout")))runOnUiThread(()->{try{resetDevice();}catch(Exception ignored){}});
                return null;
            }
            @Override public void onPageStarted(WebView view,String url,android.graphics.Bitmap icon){
                if(BuildConfig.PHOENIX_ONLY&&TrustedNavigation.trusted(url)&&!TrustedNavigation.phoenixPage(url)){view.stopLoading();loadPage(HOME+START_ROUTE);return;}
                if(TrustedNavigation.trusted(url)){lastUrl=url;beginLoading();loadStage="دریافت صفحه";}
            }
            @Override public void onPageCommitVisible(WebView view,String url){pageReady(url);}
            @Override public void onPageFinished(WebView view,String url){
                CookieManager.getInstance().flush();
                if(BuildConfig.PHOENIX_ONLY&&TrustedNavigation.phoenixPage(url))view.evaluateJavascript(PHOENIX_UI,null);
                pageReady(url);
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
                String url=request.getUrl().toString();
                if(request.isForMainFrame()&&BuildConfig.PHOENIX_ONLY&&TrustedNavigation.trusted(url)&&!TrustedNavigation.phoenixPage(url)){loadPage(HOME+START_ROUTE);return true;}
                if(TrustedNavigation.trusted(url))return false;
                if(request.isForMainFrame() && ("https".equals(request.getUrl().getScheme())||"http".equals(request.getUrl().getScheme())))try{startActivity(new Intent(Intent.ACTION_VIEW,request.getUrl()));}catch(ActivityNotFoundException ignored){}
                return true;
            }
        });
        if(WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)){
            WebViewCompat.addWebMessageListener(browser,"PhoenixNative",Collections.singleton(HOME),(view,message,origin,mainFrame,reply)->{
                if(!unlocked||!mainFrame||!TrustedNavigation.trusted(origin.toString())||!TrustedNavigation.trusted(view.getUrl()))return;
                handleMessage(message.getData(),reply);
            });
        }
        unlock();
    }
    private int dp(int n){return (int)(n*getResources().getDisplayMetrics().density);}
    private void makePageCover(){
        pageCover=new LinearLayout(this);pageCover.setOrientation(LinearLayout.VERTICAL);pageCover.setGravity(Gravity.CENTER);pageCover.setPadding(dp(34),dp(34),dp(34),dp(34));pageCover.setBackgroundColor(Color.rgb(35,26,47));
        TextView title=new TextView(this);title.setText("فونیکس");title.setTextColor(Color.WHITE);title.setTextSize(30);pageCover.addView(title);
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
    private void loadPage(String url){if(BuildConfig.PHOENIX_ONLY&&TrustedNavigation.trusted(url)&&!TrustedNavigation.phoenixPage(url))url=HOME+START_ROUTE;lastUrl=url;beginLoading();browser.loadUrl(url);}
    private boolean deviceSecure(){return getSystemService(KeyguardManager.class).isDeviceSecure();}
    private void unlock(){
        if(isFinishing())return;
        unlocked=true;browser.setVisibility(View.VISIBLE);browser.onResume();browser.requestLayout();
        if(!initialLoaded||requestedRoute!=null){String route=requestedRoute==null?START_ROUTE:requestedRoute;loadPage(HOME+route);initialLoaded=true;requestedRoute=null;}
        renderPageState();
        browser.evaluateJavascript("window.dispatchEvent(new Event('phoenix-native-resume'))",null);
    }
    private void readIntent(Intent intent){requestedRoute=START_ROUTE;}
    @Override protected void onNewIntent(Intent intent){super.onNewIntent(intent);setIntent(intent);readIntent(intent);unlock();}
    @Override protected void onResume(){super.onResume();if(PhoenixAlertService.prefs(this).getBoolean("enabled",false))try{PhoenixAlertService.start(this);}catch(Exception ignored){}getWindow().clearFlags(WindowManager.LayoutParams.FLAG_SECURE);resumed=true;unlock();}
    @Override protected void onPause(){resumed=false;CookieManager.getInstance().flush();browser.onPause();super.onPause();}
    @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results){super.onRequestPermissionsResult(request,permissions,results);if(browser!=null)browser.evaluateJavascript("window.dispatchEvent(new Event('phoenix-native-resume'))",null);}
    @Override protected void onActivityResult(int request,int result,Intent data){
        super.onActivityResult(request,result,data);
        if(request==43&&files!=null){java.util.ArrayList<Uri> values=new java.util.ArrayList<>();if(result==RESULT_OK&&data!=null){if(data.getClipData()!=null)for(int i=0;i<data.getClipData().getItemCount();i++)values.add(data.getClipData().getItemAt(i).getUri());else if(data.getData()!=null)values.add(data.getData());}files.onReceiveValue(values.isEmpty()?null:values.toArray(new Uri[0]));files=null;}
    }
    private JSONObject status() throws JSONException {
        PhoenixNotifications.channels(this);
        String id=getPreferences().getString("device-id",null);if(id==null){id=UUID.randomUUID().toString();getPreferences().edit().putString("device-id",id).apply();}
        return new JSONObject().put("deviceId",id).put("directSupported",true).put("directEnabled",PhoenixAlertService.prefs(this).getBoolean("enabled",false)).put("directRunning",PhoenixAlertService.prefs(this).getBoolean("running",false)).put("directState",PhoenixAlertService.prefs(this).getString("state","غیرفعال")).put("lastConnected",PhoenixAlertService.prefs(this).getLong("lastConnected",0)).put("deviceLock",deviceSecure()).put("notifications",PhoenixNotifications.allowed(this)).put("signalChannel",PhoenixNotifications.channelAllowed(this,PhoenixNotifications.SIGNALS)).put("pushConfigured",!FirebaseApp.getApps(this).isEmpty()).put("enrollmentId",getPreferences().getString("push-enrollment",null)).put("pushEnabled",getPreferences().getBoolean("push-enabled",false)).put("token",getPreferences().getBoolean("push-enabled",false)?getPreferences().getString("push-token",null):JSONObject.NULL);
    }
    private android.content.SharedPreferences getPreferences(){return getSharedPreferences("orbit-device",MODE_PRIVATE);}
    private void reply(JavaScriptReplyProxy proxy,String id,JSONObject value,String error){try{JSONObject out=new JSONObject().put("id",id).put("ok",error==null);if(value!=null)out.put("value",value);if(error!=null)out.put("error",error);proxy.postMessage(out.toString());}catch(Exception ignored){}}
    private void resetDevice() throws JSONException {
        PhoenixAlertService.disable(this);
        PhoenixAlertService.prefs(this).edit().clear().apply();
        getPreferences().edit().putBoolean("push-enabled",false).remove("push-token").remove("push-enrollment").apply();
        androidx.work.WorkManager.getInstance(this).cancelUniqueWork("orbit-push-registration");
        if(!FirebaseApp.getApps(this).isEmpty()){FirebaseMessaging.getInstance().setAutoInitEnabled(false);FirebaseMessaging.getInstance().deleteToken();}
    }
    private void handleMessage(String raw,JavaScriptReplyProxy proxy){
        String id="";
        try{
            if(raw==null||raw.length()>10000)throw new IllegalArgumentException("درخواست معتبر نیست.");
            JSONObject input=new JSONObject(raw);id=input.getString("id");if(id.length()>100)throw new IllegalArgumentException("شناسه معتبر نیست.");
            String command=input.getString("command");
            if(command.equals("enableDirect")){
                if(!PhoenixNotifications.channelAllowed(this,PhoenixNotifications.SIGNALS))throw new IllegalStateException("ابتدا اجازهٔ اعلان فونیکس را بده.");
                long cursor=input.getLong("cursor");String account=input.getString("account");
                if(cursor<0||account.isEmpty()||account.length()>200)throw new IllegalArgumentException("اطلاعات اتصال معتبر نیست.");
                PhoenixAlertService.prefs(this).edit().putLong("cursor",cursor).putString("account",account).putBoolean("enabled",true).commit();
                try{PhoenixAlertService.start(this);}catch(Exception e){PhoenixAlertService.disable(this);throw new IllegalStateException("شروع سرویس اعلان انجام نشد؛ برنامه را باز نگه دار و دوباره امتحان کن.");}
                reply(proxy,id,status(),null);return;
            }
            if(command.equals("disableDirect")){PhoenixAlertService.disable(this);reply(proxy,id,status(),null);return;}
            if(command.equals("testDirect")){if(!PhoenixNotifications.allowed(this))throw new IllegalStateException("اجازهٔ اعلان را فعال کن.");if(!PhoenixAlertDelivery.deliver(this,"test-"+UUID.randomUUID(),"Test",null,null,0))throw new IllegalStateException("کانال اعلان فونیکس غیرفعال است.");reply(proxy,id,status(),null);return;}
            if(command.equals("setSound")){
                String tone=input.getString("tone");if(!java.util.Arrays.asList("crystal","pulse","beacon","soft").contains(tone))throw new IllegalArgumentException("صدای معتبر انتخاب کن.");
                getSharedPreferences("phoenix-sounds",0).edit().putString("tone",tone).commit();PhoenixNotifications.channels(this);reply(proxy,id,status(),null);return;
            }
            if(command.equals("soundSettings")||command.equals("connectionSettings")){
                startActivity(new Intent(Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,getPackageName()).putExtra(Settings.EXTRA_CHANNEL_ID,command.equals("connectionSettings")?PhoenixAlertService.CHANNEL:PhoenixNotifications.selectedChannel(this)));reply(proxy,id,status(),null);return;
            }
            if(command.equals("batterySettings")){startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,Uri.parse("package:"+getPackageName())));reply(proxy,id,status(),null);return;}
            if(command.equals("configurePush")){PhoenixFirebase.configure(this,input.getJSONObject("config"));reply(proxy,id,status(),null);return;}
            if(command.equals("status")){reply(proxy,id,status(),null);return;}
            if(command.equals("requestNotifications")){
                if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},44);
                else startActivity(new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,getPackageName()));
                reply(proxy,id,new JSONObject().put("requested",true),null);return;
            }
            if(command.equals("enablePush")){
                if(FirebaseApp.getApps(this).isEmpty())throw new IllegalStateException("تنظیم سرویس Push هنوز در این نسخه انجام نشده است.");
                if(!PhoenixNotifications.channelAllowed(this,PhoenixNotifications.SIGNALS))throw new IllegalStateException("اجازهٔ اعلان و کانال فونیکس را فعال کن.");
                final String requestId=id;FirebaseMessaging.getInstance().setAutoInitEnabled(true);
                FirebaseMessaging.getInstance().getToken().addOnCompleteListener(task->{if(!task.isSuccessful()){reply(proxy,requestId,null,"دریافت توکن Push انجام نشد؛ اتصال را بررسی کن.");return;}
                    try{if(!unlocked||!TrustedNavigation.trusted(browser.getUrl())){reply(proxy,requestId,null,"برنامه قفل شده؛ دوباره امتحان کن.");return;}String enrollment=getPreferences().getString("push-enrollment",null);if(enrollment==null)enrollment=UUID.randomUUID().toString();getPreferences().edit().putString("push-enrollment",enrollment).putBoolean("push-enabled",true).putString("push-token",task.getResult()).apply();reply(proxy,requestId,status().put("token",task.getResult()),null);}catch(Exception e){reply(proxy,requestId,null,"فعال‌سازی اعلان انجام نشد.");}});return;
            }
            if(command.equals("disablePush")||command.equals("logout")){
                if(command.equals("logout"))PhoenixAlertService.disable(this);
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
