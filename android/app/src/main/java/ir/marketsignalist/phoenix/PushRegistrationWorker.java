package ir.marketsignalist.phoenix;

import android.content.Context;
import android.content.SharedPreferences;
import android.webkit.CookieManager;
import androidx.annotation.NonNull;
import androidx.work.*;
import org.json.JSONObject;
import javax.net.ssl.HttpsURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.TimeUnit;

public final class PushRegistrationWorker extends Worker {
    public PushRegistrationWorker(@NonNull Context context,@NonNull WorkerParameters params){super(context,params);}
    static void enqueue(Context context){
        OneTimeWorkRequest work=new OneTimeWorkRequest.Builder(PushRegistrationWorker.class).setConstraints(new Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).setBackoffCriteria(BackoffPolicy.EXPONENTIAL,30,TimeUnit.SECONDS).build();
        WorkManager.getInstance(context).enqueueUniqueWork("orbit-push-registration",ExistingWorkPolicy.REPLACE,work);
    }
    @NonNull @Override public Result doWork(){
        SharedPreferences preferences=getApplicationContext().getSharedPreferences("orbit-device",Context.MODE_PRIVATE);
        if(!preferences.getBoolean("push-enabled",false))return Result.success();
        String token=preferences.getString("push-token",null),device=preferences.getString("device-id",null);
        if(token==null||device==null)return Result.success();
        HttpsURLConnection connection=null;
        try{
            // The HttpOnly session remains in WebView's private cookie store. Never copy it to JS or logs.
            String cookie=CookieManager.getInstance().getCookie("https://marketsignalist.ir");
            if(cookie==null)return Result.failure();
            connection=(HttpsURLConnection)new URL("https://marketsignalist.ir/api/personal/mobile/devices").openConnection();
            connection.setConnectTimeout(15000);connection.setReadTimeout(15000);connection.setInstanceFollowRedirects(false);connection.setRequestMethod("POST");connection.setDoOutput(true);
            connection.setRequestProperty("Content-Type","application/json");connection.setRequestProperty("Cookie",cookie);
            byte[] body=new JSONObject().put("deviceId",device).put("token",token).toString().getBytes(StandardCharsets.UTF_8);
            connection.setFixedLengthStreamingMode(body.length);try(java.io.OutputStream output=connection.getOutputStream()){output.write(body);}
            int code=connection.getResponseCode();
            if(code>=200&&code<300)return Result.success();
            if(code==401||code==403||code==400)return Result.failure();
            return getRunAttemptCount()<8?Result.retry():Result.failure();
        }catch(Exception e){return getRunAttemptCount()<8?Result.retry():Result.failure();}
        finally{if(connection!=null)connection.disconnect();}
    }
}
