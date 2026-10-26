package ir.marketsignalist.phoenix;

import android.content.Context;
import android.content.SharedPreferences;
import com.google.firebase.FirebaseApp;
import com.google.firebase.FirebaseOptions;
import org.json.JSONObject;

final class PhoenixFirebase {
    static void initialize(Context context){
        if(!FirebaseApp.getApps(context).isEmpty())return;
        SharedPreferences p=context.getSharedPreferences("orbit-firebase",Context.MODE_PRIVATE);
        String app=p.getString("appId",BuildConfig.FIREBASE_APP_ID),key=p.getString("apiKey",BuildConfig.FIREBASE_API_KEY),project=p.getString("projectId",BuildConfig.FIREBASE_PROJECT_ID),sender=p.getString("senderId",BuildConfig.FIREBASE_SENDER_ID);
        if(valid(app)&&valid(key)&&valid(project)&&valid(sender))FirebaseApp.initializeApp(context,new FirebaseOptions.Builder().setApplicationId(app).setApiKey(key).setProjectId(project).setGcmSenderId(sender).build());
    }
    private static boolean valid(String value){return value!=null&&value.length()>0&&value.length()<256&&value.matches("[A-Za-z0-9:_\\-.]+");}
    static void configure(Context context,JSONObject config)throws Exception{
        String app=config.getString("appId"),key=config.getString("apiKey"),project=config.getString("projectId"),sender=config.getString("senderId");
        if(!valid(app)||!valid(key)||!valid(project)||!valid(sender))throw new IllegalArgumentException("تنظیم Firebase سرور معتبر نیست.");
        if(!FirebaseApp.getApps(context).isEmpty()){
            FirebaseOptions current=FirebaseApp.getInstance().getOptions();
            if(!app.equals(current.getApplicationId())||!project.equals(current.getProjectId()))throw new IllegalStateException("پروژهٔ اعلان تغییر کرده؛ تنظیمات برنامه باید بررسی شود.");
            return;
        }
        context.getSharedPreferences("orbit-firebase",Context.MODE_PRIVATE).edit().putString("appId",app).putString("apiKey",key).putString("projectId",project).putString("senderId",sender).commit();
        initialize(context);
    }
}
