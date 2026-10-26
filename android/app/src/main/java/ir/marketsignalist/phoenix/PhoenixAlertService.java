package ir.marketsignalist.phoenix;

import android.app.*;
import android.content.*;
import android.content.pm.ServiceInfo;
import android.os.*;
import android.webkit.CookieManager;
import org.json.*;
import javax.net.ssl.HttpsURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.time.Instant;

// User-started foreground monitoring, directly against Phoenix. No Google/Telegram dependency.
public final class PhoenixAlertService extends Service {
    static final String HOME="https://marketsignalist.ir",CHANNEL="phoenix-live-connection";
    private static final int ID=61001;
    private volatile boolean stopped;
    private volatile HttpsURLConnection connection;
    private Thread worker;
    static SharedPreferences prefs(Context c){return c.getSharedPreferences("phoenix-direct-alerts",MODE_PRIVATE);}
    static void start(Context c){c.startForegroundService(new Intent(c,PhoenixAlertService.class));}
    static void disable(Context c){prefs(c).edit().putBoolean("enabled",false).putBoolean("running",false).apply();c.stopService(new Intent(c,PhoenixAlertService.class));}
    @Override public android.os.IBinder onBind(Intent intent){return null;}
    @Override public int onStartCommand(Intent intent,int flags,int id){
        if(intent!=null&&"stop".equals(intent.getAction())){disable(this);return START_NOT_STICKY;}
        if(!prefs(this).getBoolean("enabled",false)||!PhoenixNotifications.allowed(this)){stopSelf();return START_NOT_STICKY;}
        NotificationChannel channel=new NotificationChannel(CHANNEL,"اتصال زندهٔ اعلان فونیکس",NotificationManager.IMPORTANCE_LOW);
        getSystemService(NotificationManager.class).createNotificationChannel(channel);
        if(Build.VERSION.SDK_INT>=34)startForeground(ID,ongoing("در حال اتصال به فونیکس…"),ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE);
        else startForeground(ID,ongoing("در حال اتصال به فونیکس…"));
        prefs(this).edit().putBoolean("running",true).apply();
        if(worker==null){prefs(this).edit().putString("state","در حال اتصال به فونیکس…").apply();worker=new Thread(this::monitor,"phoenix-alert-monitor");worker.start();}
        return START_STICKY;
    }
    private Notification ongoing(String text){
        PendingIntent open=PendingIntent.getActivity(this,61001,new Intent(this,MainActivity.class),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        PendingIntent stop=PendingIntent.getService(this,61002,new Intent(this,PhoenixAlertService.class).setAction("stop"),PendingIntent.FLAG_IMMUTABLE|PendingIntent.FLAG_UPDATE_CURRENT);
        return new Notification.Builder(this,CHANNEL).setSmallIcon(android.R.drawable.ic_dialog_info).setContentTitle("اعلان زندهٔ فونیکس").setContentText(text).setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true).addAction(new Notification.Action.Builder(null,"توقف اعلان زنده",stop).build()).build();
    }
    private void state(String text){prefs(this).edit().putString("state",text).apply();getSystemService(NotificationManager.class).notify(ID,ongoing(text));}
    private void monitor(){
        int failures=0;
        try{
            while(!stopped&&prefs(this).getBoolean("enabled",false)){
                try{
                    if(!PhoenixNotifications.channelAllowed(this,PhoenixNotifications.SIGNALS)){state("اجازهٔ اعلان غیرفعال است؛ برنامه را باز کن");break;}
                    String cookie=CookieManager.getInstance().getCookie(HOME);
                    if(cookie==null){state("برای ادامه وارد فونیکس شو");break;}
                    long cursor=prefs(this).getLong("cursor",0);
                    connection=(HttpsURLConnection)new URL(HOME+"/api/mobile/feed?after="+cursor+"&wait=20").openConnection();
                    connection.setInstanceFollowRedirects(false);connection.setConnectTimeout(10000);connection.setReadTimeout(30000);connection.setRequestProperty("Cookie",cookie);connection.setRequestProperty("Accept","application/json");
                    int code=connection.getResponseCode();
                    if(code==401||code==403||code==302){state("نشست پایان یافته؛ وارد فونیکس شو");prefs(this).edit().putBoolean("enabled",false).apply();break;}
                    if(code!=200)throw new java.io.IOException("Server unavailable");
                    if(stopped||!prefs(this).getBoolean("enabled",false))break;
                    String raw;try(java.io.InputStream input=connection.getInputStream()){
                        java.io.ByteArrayOutputStream output=new java.io.ByteArrayOutputStream();byte[] buffer=new byte[4096];int count;while((count=input.read(buffer))!=-1){if(output.size()+count>262144)throw new java.io.IOException("Feed too large");output.write(buffer,0,count);}raw=output.toString("UTF-8");
                    }
                    JSONObject page=new JSONObject(raw);
                    if(!page.getString("account").equalsIgnoreCase(prefs(this).getString("account",""))){state("حساب تغییر کرده؛ اعلان را دوباره فعال کن");prefs(this).edit().putBoolean("enabled",false).apply();break;}
                    if(!consume(this,page))break;
                    failures=0;state("متصل به فونیکس؛ منتظر رویداد سیگنال");
                    Thread.sleep(250);
                }catch(InterruptedException e){break;}
                catch(Exception e){if(stopped)break;state("اتصال قطع است؛ تلاش مجدد…");try{Thread.sleep(Math.min(60000,2000L<<Math.min(failures++,5)));}catch(InterruptedException interrupted){break;}}
                finally{if(connection!=null){connection.disconnect();connection=null;}}
            }
        }finally{prefs(this).edit().putBoolean("running",false).apply();stopForeground(STOP_FOREGROUND_REMOVE);stopSelf();}
    }
    static boolean consume(Context context,JSONObject page)throws Exception{
        SharedPreferences p=prefs(context);if(!p.getBoolean("enabled",false))return false;
        if(!page.getString("account").equalsIgnoreCase(p.getString("account","")))throw new java.io.IOException("Account mismatch");
        long cursor=p.getLong("cursor",0),next=page.getLong("cursor");if(next<cursor)throw new java.io.IOException("Cursor moved backwards");
        JSONArray events=page.getJSONArray("events");
        for(int i=0;i<events.length();i++){
            if(!p.getBoolean("enabled",false))return false;
            JSONObject event=events.getJSONObject(i);
            if(!PhoenixAlertDelivery.deliver(context,event.getString("id"),event.getString("kind"),event.optString("symbol"),event.optString("exitKind"),Instant.parse(event.getString("at")).toEpochMilli()))throw new java.io.IOException("Notification unavailable");
        }
        return p.edit().putLong("cursor",next).putLong("lastConnected",System.currentTimeMillis()).commit();
    }
    @Override public void onDestroy(){stopped=true;prefs(this).edit().putBoolean("running",false).apply();if(connection!=null)connection.disconnect();if(worker!=null)worker.interrupt();super.onDestroy();}
}
