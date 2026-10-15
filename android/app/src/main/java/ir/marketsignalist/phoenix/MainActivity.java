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
import androidx.biometric.BiometricManager;
import androidx.biometric.BiometricPrompt;
import androidx.fragment.app.FragmentActivity;
import androidx.webkit.*;
import com.google.firebase.FirebaseApp;
import com.google.firebase.messaging.FirebaseMessaging;
import org.json.*;
import java.util.Collections;
import java.util.UUID;

public final class MainActivity extends FragmentActivity {
    private static final String HOME="https://marketsignalist.ir";
    private WebView browser;
    private FrameLayout root;
    private LinearLayout cover;
    private TextView lockStatus;
    private boolean unlocked=false, authenticating=false, initialLoaded=false;
    private String requestedRoute="/";
    private boolean openReminders=false;
    private BiometricPrompt biometric;
    private boolean credentialActivity=false, resumed=false, credentialVerified=false;
    private android.webkit.ValueCallback<Uri[]> files;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_SECURE);
        getWindow().setStatusBarColor(Color.rgb(16,13,26));getWindow().setNavigationBarColor(Color.rgb(16,13,26));
        root=new FrameLayout(this);root.setBackgroundColor(Color.rgb(16,13,26));
        root.setOnApplyWindowInsetsListener((view,insets)->{view.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());return insets;});
        browser=new WebView(this);browser.setBackgroundColor(Color.rgb(16,13,26));browser.setOverScrollMode(View.OVER_SCROLL_NEVER);
        root.addView(browser,new FrameLayout.LayoutParams(-1,-1));makeCover();setContentView(root);readIntent(getIntent());
        WebSettings settings=browser.getSettings();settings.setJavaScriptEnabled(true);settings.setDomStorageEnabled(true);
        settings.setUserAgentString(settings.getUserAgentString()+" PhoenixAndroid/1.3");settings.setAllowFileAccess(false);settings.setAllowContentAccess(false);settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);settings.setJavaScriptCanOpenWindowsAutomatically(false);
        CookieManager.getInstance().setAcceptCookie(true);CookieManager.getInstance().setAcceptThirdPartyCookies(browser,false);
        browser.setWebChromeClient(new WebChromeClient(){
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
            @Override public void onPageFinished(WebView view,String url){CookieManager.getInstance().flush();}
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
        // Restore neither unlocked state nor a rendered web snapshot after process death.
        showLock();
    }
    private int dp(int n){return (int)(n*getResources().getDisplayMetrics().density);}
    private void makeCover(){
        cover=new LinearLayout(this);cover.setOrientation(LinearLayout.VERTICAL);cover.setGravity(Gravity.CENTER);cover.setPadding(dp(34),dp(34),dp(34),dp(34));cover.setBackgroundColor(Color.rgb(16,13,26));
        TextView title=new TextView(this);title.setText("مدار و فونیکس");title.setTextColor(Color.WHITE);title.setTextSize(30);title.setGravity(Gravity.CENTER);cover.addView(title);
        lockStatus=new TextView(this);lockStatus.setText("برای ورود، هویتت را با قفل امن گوشی تأیید کن.");lockStatus.setTextColor(Color.rgb(215,194,230));lockStatus.setTextSize(16);lockStatus.setGravity(Gravity.CENTER);lockStatus.setPadding(0,dp(21),0,dp(21));cover.addView(lockStatus);
        Button unlock=new Button(this);unlock.setText("بازکردن با اثر انگشت یا رمز گوشی");unlock.setTextColor(Color.rgb(38,28,48));unlock.setBackgroundTintList(android.content.res.ColorStateList.valueOf(Color.rgb(220,250,82)));unlock.setOnClickListener(v->authenticate());cover.addView(unlock);
        Button credential=new Button(this);credential.setText("استفاده از رمز / الگوی گوشی");credential.setOnClickListener(v->credential());cover.addView(credential);
        root.addView(cover,new FrameLayout.LayoutParams(-1,-1));
    }
    private void showLock(){unlocked=false;cover.setVisibility(View.VISIBLE);browser.setVisibility(View.INVISIBLE);}
    private boolean deviceSecure(){return getSystemService(KeyguardManager.class).isDeviceSecure();}
    private void authenticate(){
        if(authenticating||isFinishing())return;
        if(!deviceSecure()){lockStatus.setText("ابتدا برای گوشی رمز، الگو یا اثر انگشت تنظیم کن؛ برنامه بدون قفل دستگاه باز نمی‌شود.");startActivity(new Intent(Settings.ACTION_SECURITY_SETTINGS));return;}
        int strong=BiometricManager.Authenticators.BIOMETRIC_STRONG;
        int allowed=Build.VERSION.SDK_INT>=30?strong|BiometricManager.Authenticators.DEVICE_CREDENTIAL:strong;
        if(BiometricManager.from(this).canAuthenticate(allowed)!=BiometricManager.BIOMETRIC_SUCCESS){credential();return;}
        authenticating=true;
        biometric=new BiometricPrompt(this,androidx.core.content.ContextCompat.getMainExecutor(this),new BiometricPrompt.AuthenticationCallback(){
            @Override public void onAuthenticationSucceeded(@NonNull BiometricPrompt.AuthenticationResult result){authenticating=false;if(resumed)unlock();else showLock();}
            @Override public void onAuthenticationError(int code,@NonNull CharSequence text){authenticating=false;showLock();if(code==BiometricPrompt.ERROR_NEGATIVE_BUTTON||code==BiometricPrompt.ERROR_LOCKOUT||code==BiometricPrompt.ERROR_LOCKOUT_PERMANENT)credential();else lockStatus.setText("ورود تأیید نشد؛ دوباره تلاش کن یا رمز گوشی را بزن.");}
            @Override public void onAuthenticationFailed(){lockStatus.setText("اثر انگشت تأیید نشد؛ دوباره تلاش کن یا رمز گوشی را بزن.");}
        });
        BiometricPrompt.PromptInfo.Builder builder=new BiometricPrompt.PromptInfo.Builder().setTitle("بازکردن مدار و فونیکس").setSubtitle("تأیید هویت با امنیت خود گوشی").setAllowedAuthenticators(allowed);
        if(Build.VERSION.SDK_INT<30)builder.setNegativeButtonText("رمز گوشی");
        biometric.authenticate(builder.build());
    }
    private void credential(){
        if(authenticating||isFinishing())return;
        Intent intent=getSystemService(KeyguardManager.class).createConfirmDeviceCredentialIntent("مدار و فونیکس","رمز، الگو یا PIN گوشی را وارد کن");
        if(intent==null){lockStatus.setText("قفل امن گوشی فعال نیست.");startActivity(new Intent(Settings.ACTION_SECURITY_SETTINGS));return;}
        authenticating=true;credentialActivity=true;startActivityForResult(intent,42);
    }
    private void unlock(){
        if(isFinishing())return;
        unlocked=true;cover.setVisibility(View.GONE);browser.setVisibility(View.VISIBLE);browser.onResume();
        if(!initialLoaded||requestedRoute!=null){String route=requestedRoute==null?"/":requestedRoute;browser.loadUrl(HOME+route+(openReminders&&"/".equals(route)?"?reminders=1":""));initialLoaded=true;requestedRoute=null;openReminders=false;}
        browser.evaluateJavascript("window.dispatchEvent(new Event('orbit-native-resume'))",null);
    }
    private void readIntent(Intent intent){if(intent==null)return;requestedRoute=TrustedNavigation.route(intent.getStringExtra("route"));openReminders=intent.getBooleanExtra("reminders",false);}
    @Override protected void onNewIntent(Intent intent){super.onNewIntent(intent);setIntent(intent);readIntent(intent);showLock();}
    @Override protected void onResume(){super.onResume();resumed=true;if(credentialVerified){credentialVerified=false;unlock();return;}if(!unlocked&&!authenticating){showLock();new Handler(Looper.getMainLooper()).post(this::authenticate);}}
    @Override protected void onPause(){resumed=false;CookieManager.getInstance().flush();showLock();browser.onPause();super.onPause();}
    @Override protected void onStop(){showLock();if(authenticating&&!credentialActivity&&biometric!=null)biometric.cancelAuthentication();super.onStop();}
    @Override protected void onActivityResult(int request,int result,Intent data){
        super.onActivityResult(request,result,data);
        if(request==42){credentialActivity=false;authenticating=false;if(result==RESULT_OK){if(resumed)unlock();else credentialVerified=true;}else{showLock();lockStatus.setText("برای ورود باید قفل دستگاه تأیید شود.");}}
        if(request==43&&files!=null){java.util.ArrayList<Uri> values=new java.util.ArrayList<>();if(result==RESULT_OK&&data!=null){if(data.getClipData()!=null)for(int i=0;i<data.getClipData().getItemCount();i++)values.add(data.getClipData().getItemAt(i).getUri());else if(data.getData()!=null)values.add(data.getData());}files.onReceiveValue(values.isEmpty()?null:values.toArray(new Uri[0]));files=null;}
    }
    private JSONObject status() throws JSONException {
        OrbitNotifications.channels(this);
        String id=getPreferences().getString("device-id",null);if(id==null){id=UUID.randomUUID().toString();getPreferences().edit().putString("device-id",id).apply();}
        return new JSONObject().put("deviceId",id).put("deviceLock",deviceSecure()).put("notifications",OrbitNotifications.allowed(this)).put("reminderChannel",OrbitNotifications.channelAllowed(this,OrbitNotifications.REMINDERS)).put("signalChannel",OrbitNotifications.channelAllowed(this,OrbitNotifications.SIGNALS)).put("exactAlarms",Reminders.exactAllowed(this)).put("pushConfigured",!FirebaseApp.getApps(this).isEmpty()).put("pushEnabled",getPreferences().getBoolean("push-enabled",false)).put("token",getPreferences().getBoolean("push-enabled",false)?getPreferences().getString("push-token",null):JSONObject.NULL).put("reminders",Reminders.list(this));
    }
    private android.content.SharedPreferences getPreferences(){return getSharedPreferences("orbit-device",MODE_PRIVATE);}
    private void reply(JavaScriptReplyProxy proxy,String id,JSONObject value,String error){try{JSONObject out=new JSONObject().put("id",id).put("ok",error==null);if(value!=null)out.put("value",value);if(error!=null)out.put("error",error);proxy.postMessage(out.toString());}catch(Exception ignored){}}
    private void resetDevice() throws JSONException {
        getPreferences().edit().putBoolean("push-enabled",false).remove("push-token").apply();
        if(!FirebaseApp.getApps(this).isEmpty()){FirebaseMessaging.getInstance().setAutoInitEnabled(false);FirebaseMessaging.getInstance().deleteToken();}
        Reminders.clear(this);
    }
    private void handleMessage(String raw,JavaScriptReplyProxy proxy){
        String id="";
        try{
            if(raw==null||raw.length()>10000)throw new IllegalArgumentException("درخواست معتبر نیست.");
            JSONObject input=new JSONObject(raw);id=input.getString("id");if(id.length()>100)throw new IllegalArgumentException("شناسه معتبر نیست.");
            String command=input.getString("command");
            if(command.equals("status")){reply(proxy,id,status(),null);return;}
            if(command.equals("requestNotifications")){
                if(Build.VERSION.SDK_INT>=33&&checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},44);
                else startActivity(new Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,getPackageName()));
                reply(proxy,id,new JSONObject().put("requested",true),null);return;
            }
            if(command.equals("requestExactAlarms")){if(Build.VERSION.SDK_INT>=31&&!Reminders.exactAllowed(this))startActivity(new Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM,Uri.parse("package:"+getPackageName())));reply(proxy,id,new JSONObject().put("requested",true),null);return;}
            if(command.equals("schedule")){reply(proxy,id,Reminders.schedule(this,input.getString("reminderId"),input.getString("title"),input.getLong("at")),null);return;}
            if(command.equals("cancel")){Reminders.cancel(this,input.getString("reminderId"));reply(proxy,id,status(),null);return;}
            if(command.equals("testNotification")){if(!OrbitNotifications.channelAllowed(this,OrbitNotifications.REMINDERS))throw new IllegalStateException("اجازهٔ اعلان و کانال یادآوری را فعال کن.");OrbitNotifications.show(this,OrbitNotifications.REMINDERS,UUID.randomUUID().toString(),"/");reply(proxy,id,new JSONObject().put("shown",true),null);return;}
            if(command.equals("enablePush")){
                if(FirebaseApp.getApps(this).isEmpty())throw new IllegalStateException("تنظیم سرویس Push هنوز در این نسخه انجام نشده است.");
                if(!OrbitNotifications.channelAllowed(this,OrbitNotifications.SIGNALS))throw new IllegalStateException("اجازهٔ اعلان و کانال فونیکس را فعال کن.");
                final String requestId=id;FirebaseMessaging.getInstance().setAutoInitEnabled(true);
                FirebaseMessaging.getInstance().getToken().addOnCompleteListener(task->{if(!task.isSuccessful()){reply(proxy,requestId,null,"دریافت توکن Push انجام نشد؛ اتصال را بررسی کن.");return;}
                    try{if(!unlocked||!TrustedNavigation.trusted(browser.getUrl())){reply(proxy,requestId,null,"برنامه قفل شده؛ دوباره امتحان کن.");return;}getPreferences().edit().putBoolean("push-enabled",true).putString("push-token",task.getResult()).apply();reply(proxy,requestId,status().put("token",task.getResult()),null);}catch(Exception e){reply(proxy,requestId,null,"فعال‌سازی اعلان انجام نشد.");}});return;
            }
            if(command.equals("disablePush")||command.equals("logout")){
                getPreferences().edit().putBoolean("push-enabled",false).remove("push-token").apply();
                if(!FirebaseApp.getApps(this).isEmpty()){FirebaseMessaging.getInstance().setAutoInitEnabled(false);FirebaseMessaging.getInstance().deleteToken();}
                if(command.equals("logout"))resetDevice();
                reply(proxy,id,new JSONObject().put("disabled",true),null);return;
            }
            if(command.equals("lock")){reply(proxy,id,new JSONObject().put("locked",true),null);showLock();return;}
            throw new IllegalArgumentException("این فرمان پشتیبانی نمی‌شود.");
        }catch(Exception e){reply(proxy,id,null,e instanceof JSONException?"ساختار درخواست معتبر نیست.":e.getMessage());}
    }
    @Override public void onBackPressed(){if(!unlocked){finish();return;}if(browser.canGoBack())browser.goBack();else super.onBackPressed();}
    @Override protected void onDestroy(){if(files!=null)files.onReceiveValue(null);if(biometric!=null)biometric.cancelAuthentication();browser.destroy();super.onDestroy();}
}
