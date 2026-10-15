package ir.marketsignalist.phoenix;

import android.app.*;
import android.content.*;
import android.net.Uri;
import android.os.Build;
import org.json.*;
import java.time.*;
import java.util.*;

final class Reminders {
    static JSONArray list(Context c) throws JSONException {return new JSONArray(SecureStore.get(c,"reminders","[]"));}
    static boolean exactAllowed(Context c){return Build.VERSION.SDK_INT<31 || c.getSystemService(AlarmManager.class).canScheduleExactAlarms();}
    private static PendingIntent intent(Context c,String id,boolean snooze) {
        return PendingIntent.getBroadcast(c,0,new Intent(c,ReminderReceiver.class).setAction("orbit.reminder").setData(Uri.parse("orbit://reminder/"+id+(snooze?"/snooze":""))).putExtra("id",id).putExtra("snooze",snooze),PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);
    }
    private static void arm(Context c,JSONObject row,boolean snooze) throws JSONException {
        c.getSystemService(AlarmManager.class).setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP,row.getLong(snooze?"snoozeAt":"at"),intent(c,row.getString("id"),snooze));
    }
    private static void disarm(Context c,String id){for(boolean snooze:new boolean[]{false,true})c.getSystemService(AlarmManager.class).cancel(intent(c,id,snooze));}
    private static void permissions(Context c){
        if(!OrbitNotifications.channelAllowed(c,OrbitNotifications.REMINDERS))throw new IllegalStateException("ابتدا اجازهٔ اعلان و کانال یادآوری را فعال کن.");
        if(!exactAllowed(c))throw new IllegalStateException("ابتدا دسترسی آلارم دقیق را فعال کن.");
    }
    private static Set<Integer> days(JSONObject row) throws JSONException {
        Set<Integer> result=new HashSet<>();JSONArray values=row.optJSONArray("days");
        if(values!=null)for(int i=0;i<values.length();i++){int day=values.getInt(i);if(day<1||day>7)throw new IllegalArgumentException("روز تکرار معتبر نیست.");result.add(day);}
        return result;
    }
    private static long next(JSONObject row,long after) throws JSONException {
        return ReminderSchedule.next(Math.max(after,row.optLong("lastDeliveredAt")),row.getLong("startAt"),row.getInt("hour"),row.getInt("minute"),days(row),ZoneId.systemDefault());
    }
    static synchronized JSONObject schedule(Context c,JSONObject input) throws JSONException {
        String id=input.getString("reminderId"),title=input.getString("title"),repeat=input.optString("repeat","none"),entryId=input.optString("entryId",""),entryKind=input.optString("entryKind","");
        UUID.fromString(id);if(!entryId.isEmpty())UUID.fromString(entryId);
        if(!entryId.isEmpty()&&!Arrays.asList("task","habit","goal","project","decision","vehicle","finance","craft","experiment","journal","time","review","checkin").contains(entryKind))throw new IllegalArgumentException("ثبت مرتبط معتبر نیست.");
        if(title.trim().isEmpty()||title.length()>200)throw new IllegalArgumentException("عنوان یادآوری معتبر نیست.");
        if(!Arrays.asList("none","daily","weekly").contains(repeat))throw new IllegalArgumentException("تکرار معتبر نیست.");
        long at=input.getLong("at"),now=System.currentTimeMillis();
        if(at<now+15000||at>now+366L*86400000)throw new IllegalArgumentException("زمان را بین ۱۵ ثانیهٔ آینده و یک سال انتخاب کن.");
        permissions(c);
        ZonedDateTime local=Instant.ofEpochMilli(at).atZone(ZoneId.systemDefault());
        JSONObject row=new JSONObject().put("id",id).put("title",title.trim()).put("at",at).put("startAt",at).put("hour",local.getHour()).put("minute",local.getMinute()).put("repeat",repeat).put("days",repeat.equals("weekly")?input.optJSONArray("days"):new JSONArray()).put("entryId",entryId).put("entryKind",entryKind).put("delivered",false);
        if(repeat.equals("weekly")&&days(row).isEmpty())throw new IllegalArgumentException("حداقل یک روز هفته را انتخاب کن.");
        if(repeat.equals("weekly")&&!days(row).contains(local.getDayOfWeek().getValue()))row.put("at",next(row,now+14999));
        JSONArray rows=list(c),updated=new JSONArray();JSONObject previous=null;
        for(int i=0;i<rows.length();i++){JSONObject old=rows.getJSONObject(i);if(old.getString("id").equals(id))previous=old;else updated.put(old);}
        if(updated.length()>=100)throw new IllegalArgumentException("ابتدا چند یادآوری قبلی را حذف کن.");
        updated.put(row);SecureStore.put(c,"reminders",updated.toString());disarm(c,id);
        try{arm(c,row,false);}catch(RuntimeException e){SecureStore.put(c,"reminders",rows.toString());if(previous!=null)try{if(!previous.optBoolean("delivered"))arm(c,previous,false);if(previous.optLong("snoozeAt")>now)arm(c,previous,true);}catch(Exception ignored){}throw e;}
        c.getSystemService(NotificationManager.class).cancel(id,1);return row;
    }
    static synchronized void cancel(Context c,String id) throws JSONException {
        UUID.fromString(id);JSONArray rows=list(c),updated=new JSONArray();
        for(int i=0;i<rows.length();i++)if(!rows.getJSONObject(i).getString("id").equals(id))updated.put(rows.getJSONObject(i));
        SecureStore.put(c,"reminders",updated.toString());disarm(c,id);c.getSystemService(NotificationManager.class).cancel(id,1);
    }
    static synchronized void snooze(Context c,String id,int minutes) throws JSONException {
        UUID.fromString(id);if(minutes!=10&&minutes!=30&&minutes!=60)throw new IllegalArgumentException("زمان تعویق معتبر نیست.");permissions(c);
        JSONArray rows=list(c);for(int i=0;i<rows.length();i++){JSONObject row=rows.getJSONObject(i);if(!row.getString("id").equals(id))continue;
            if(!row.optBoolean("delivered")&&row.optLong("lastDeliveredAt")==0)throw new IllegalArgumentException("هنوز موعد این یادآوری نرسیده است.");
            String before=rows.toString();row.put("snoozeAt",System.currentTimeMillis()+minutes*60000L);SecureStore.put(c,"reminders",rows.toString());
            try{arm(c,row,true);}catch(RuntimeException e){SecureStore.put(c,"reminders",before);throw e;}
            c.getSystemService(NotificationManager.class).cancel(id,1);return;
        }throw new IllegalArgumentException("یادآوری پیدا نشد.");
    }
    static synchronized void deliver(Context c,String id) throws JSONException {deliver(c,id,false);}
    static synchronized void deliver(Context c,String id,boolean snooze) throws JSONException {
        JSONArray rows=list(c);long now=System.currentTimeMillis();
        for(int i=0;i<rows.length();i++){JSONObject row=rows.getJSONObject(i);if(!row.getString("id").equals(id))continue;
            long at=row.optLong(snooze?"snoozeAt":"at");if(at==0||at>now+1000||(!snooze&&row.optBoolean("delivered")))return;
            if(snooze)row.remove("snoozeAt");
            else if(!row.optString("repeat","none").equals("none"))row.put("at",next(row,now));
            else row.put("delivered",true);
            row.put("lastDeliveredAt",now);SecureStore.put(c,"reminders",rows.toString());
            if(!snooze&&!row.optBoolean("delivered")&&exactAllowed(c))arm(c,row,false);
            c.getSystemService(NotificationManager.class).cancel(id,1);
            OrbitNotifications.show(c,OrbitNotifications.REMINDERS,id,"/");return;
        }
    }
    static synchronized void restore(Context c) throws JSONException {
        JSONArray snapshot=list(c);long now=System.currentTimeMillis();
        for(int i=0;i<snapshot.length();i++){
            JSONArray rows=list(c);JSONObject row=rows.getJSONObject(i);String id=row.getString("id");
            if(!row.optBoolean("delivered")){
                if(row.getLong("at")<=now&&now-row.getLong("at")<86400000){
                    deliver(c,id,false);rows=list(c);row=rows.getJSONObject(i);
                }else{
                    if(!row.optString("repeat","none").equals("none"))row.put("at",next(row,now));
                    else if(row.getLong("at")<=now)row.put("delivered",true);
                    SecureStore.put(c,"reminders",rows.toString());
                }
                if(!row.optBoolean("delivered")&&exactAllowed(c))arm(c,row,false);
            }
            if(row.optLong("snoozeAt")>now){if(exactAllowed(c))arm(c,row,true);}
            else if(row.optLong("snoozeAt")>0){
                if(now-row.getLong("snoozeAt")<86400000)deliver(c,id,true);
                else {row.remove("snoozeAt");SecureStore.put(c,"reminders",rows.toString());}
            }
        }
    }
    static synchronized void clear(Context c) throws JSONException {JSONArray rows=list(c);for(int i=0;i<rows.length();i++)disarm(c,rows.getJSONObject(i).getString("id"));SecureStore.put(c,"reminders","[]");c.getSystemService(NotificationManager.class).cancelAll();}
}
