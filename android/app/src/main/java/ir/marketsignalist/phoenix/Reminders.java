package ir.marketsignalist.phoenix;

import android.app.*;
import android.content.*;
import android.net.Uri;
import android.os.Build;
import org.json.*;
import java.util.UUID;

final class Reminders {
    static JSONArray list(Context c) throws JSONException {return new JSONArray(SecureStore.get(c,"reminders","[]"));}
    static boolean exactAllowed(Context c){return Build.VERSION.SDK_INT<31 || c.getSystemService(AlarmManager.class).canScheduleExactAlarms();}
    private static PendingIntent intent(Context c,String id) {return PendingIntent.getBroadcast(c,0,new Intent(c,ReminderReceiver.class).setAction("orbit.reminder").setData(Uri.parse("orbit://reminder/"+id)).putExtra("id",id),PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);}
    private static void arm(Context c,JSONObject row) throws JSONException {c.getSystemService(AlarmManager.class).setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP,row.getLong("at"),intent(c,row.getString("id")));}
    static synchronized JSONObject schedule(Context c,String id,String title,long at) throws JSONException {
        UUID.fromString(id);
        if(title==null || title.trim().isEmpty() || title.length()>200)throw new IllegalArgumentException("عنوان یادآوری معتبر نیست.");
        long now=System.currentTimeMillis();if(at<now+15000 || at>now+366L*86400000)throw new IllegalArgumentException("زمان را بین ۱۵ ثانیهٔ آینده و یک سال انتخاب کن.");
        if(!OrbitNotifications.channelAllowed(c,OrbitNotifications.REMINDERS))throw new IllegalStateException("ابتدا اجازهٔ اعلان و کانال یادآوری را فعال کن.");
        if(!exactAllowed(c))throw new IllegalStateException("ابتدا دسترسی آلارم دقیق را فعال کن.");
        JSONArray rows=list(c);JSONArray updated=new JSONArray();
        for(int i=0;i<rows.length();i++)if(!rows.getJSONObject(i).getString("id").equals(id))updated.put(rows.getJSONObject(i));
        if(updated.length()>=100)throw new IllegalArgumentException("ابتدا چند یادآوری قبلی را حذف کن.");
        JSONObject row=new JSONObject().put("id",id).put("title",title.trim()).put("at",at).put("delivered",false);updated.put(row);
        SecureStore.put(c,"reminders",updated.toString());
        try{arm(c,row);}catch(RuntimeException e){SecureStore.put(c,"reminders",rows.toString());throw e;}
        return row;
    }
    static synchronized void cancel(Context c,String id) throws JSONException {
        UUID.fromString(id);c.getSystemService(AlarmManager.class).cancel(intent(c,id));
        JSONArray rows=list(c),updated=new JSONArray();for(int i=0;i<rows.length();i++)if(!rows.getJSONObject(i).getString("id").equals(id))updated.put(rows.getJSONObject(i));
        SecureStore.put(c,"reminders",updated.toString());
        c.getSystemService(NotificationManager.class).cancel(id,1);
    }
    static synchronized void deliver(Context c,String id) throws JSONException {
        JSONArray rows=list(c);for(int i=0;i<rows.length();i++){
            JSONObject row=rows.getJSONObject(i);
            if(row.getString("id").equals(id)&&!row.optBoolean("delivered")){
                if(row.getLong("at")>System.currentTimeMillis()+1000)return;
                row.put("delivered",true);SecureStore.put(c,"reminders",rows.toString());
                OrbitNotifications.show(c,OrbitNotifications.REMINDERS,id,"/");return;
            }
        }
    }
    static synchronized void restore(Context c) throws JSONException {
        JSONArray rows=list(c);long now=System.currentTimeMillis();
        for(int i=0;i<rows.length();i++){JSONObject row=rows.getJSONObject(i);if(row.optBoolean("delivered"))continue;
            if(row.getLong("at")<=now){if(now-row.getLong("at")<86400000)deliver(c,row.getString("id"));}
            else if(exactAllowed(c))arm(c,row);
        }
    }
    static synchronized void clear(Context c) throws JSONException {JSONArray rows=list(c);for(int i=0;i<rows.length();i++)c.getSystemService(AlarmManager.class).cancel(intent(c,rows.getJSONObject(i).getString("id")));SecureStore.put(c,"reminders","[]");c.getSystemService(NotificationManager.class).cancelAll();}
}
