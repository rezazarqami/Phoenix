package ir.marketsignalist.phoenix;

import android.app.*;
import android.content.Context;
import org.json.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.*;
import java.util.*;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28,shadows=RemindersTest.PrivateStore.class)
public class RemindersTest {
    // Exercise scheduling/receiver logic without emulating the hardware Android Keystore.
    @Implements(value=SecureStore.class,isInAndroidSdk=false)
    public static class PrivateStore {
        @Implementation protected static void put(Context c,String name,String value){c.getSharedPreferences("fixture-reminders",0).edit().putString(name,value).commit();}
        @Implementation protected static String get(Context c,String name,String fallback){return c.getSharedPreferences("fixture-reminders",0).getString(name,fallback);}
    }
    private Context c;private TimeZone original;
    @Before public void setup(){original=TimeZone.getDefault();TimeZone.setDefault(TimeZone.getTimeZone("UTC"));c=RuntimeEnvironment.getApplication();OrbitNotifications.channels(c);}
    @After public void finish(){TimeZone.setDefault(original);}
    private JSONObject input(String id,String repeat) throws Exception {return new JSONObject().put("reminderId",id).put("title","Private reminder").put("at",System.currentTimeMillis()+120000).put("repeat",repeat);}
    private void due(String field,long age) throws Exception {
        JSONArray rows=Reminders.list(c);rows.getJSONObject(0).put(field,System.currentTimeMillis()-age);SecureStore.put(c,"reminders",rows.toString());
    }
    @Test public void editingReplacesAlarmAndStableIdentity()throws Exception {
        String id=UUID.randomUUID().toString();Reminders.schedule(c,input(id,"none"));Reminders.schedule(c,input(id,"daily").put("title","Edited"));
        assertEquals(1,Reminders.list(c).length());assertEquals("Edited",Reminders.list(c).getJSONObject(0).getString("title"));
        assertEquals(1,shadowOf(c.getSystemService(AlarmManager.class)).getScheduledAlarms().size());
    }
    @Test public void recurringDeliveryDeduplicatesAndSnoozePreservesNextOccurrence()throws Exception {
        String id=UUID.randomUUID().toString();JSONObject request=input(id,"daily"),saved=Reminders.schedule(c,request);
        assertTrue("First occurrence skipped",saved.getLong("at")-request.getLong("at")<60000);
        Reminders.deliver(c,id);assertEquals(0,shadowOf(c.getSystemService(NotificationManager.class)).getAllNotifications().size());
        due("at",60000);Reminders.deliver(c,id);
        JSONObject row=Reminders.list(c).getJSONObject(0);long next=row.getLong("at");assertTrue(next>System.currentTimeMillis());assertFalse(row.getBoolean("delivered"));
        long last=row.getLong("lastDeliveredAt");Reminders.deliver(c,id);assertEquals(last,Reminders.list(c).getJSONObject(0).getLong("lastDeliveredAt"));
        Reminders.snooze(c,id,10);assertEquals(next,Reminders.list(c).getJSONObject(0).getLong("at"));
        due("snoozeAt",60000);Reminders.deliver(c,id,true);row=Reminders.list(c).getJSONObject(0);assertFalse(row.has("snoozeAt"));assertEquals(next,row.getLong("at"));
        Reminders.cancel(c,id);assertEquals(0,Reminders.list(c).length());assertEquals(0,shadowOf(c.getSystemService(AlarmManager.class)).getScheduledAlarms().size());
        Reminders.deliver(c,id);assertEquals(0,shadowOf(c.getSystemService(NotificationManager.class)).getAllNotifications().size());
    }
    @Test public void restoreSkipsOldDailyMissesAndRearmsExactlyOneFutureOccurrence()throws Exception {
        String id=UUID.randomUUID().toString();Reminders.schedule(c,input(id,"daily"));due("at",4L*86400000);Reminders.restore(c);
        assertTrue(Reminders.list(c).getJSONObject(0).getLong("at")>System.currentTimeMillis());
        assertEquals(0,shadowOf(c.getSystemService(NotificationManager.class)).getAllNotifications().size());
        assertEquals(1,shadowOf(c.getSystemService(AlarmManager.class)).getScheduledAlarms().size());
    }
    @Test public void oldOneTimeRowsRemainReadableAndCannotRepeat()throws Exception {
        String id=UUID.randomUUID().toString();SecureStore.put(c,"reminders",new JSONArray().put(new JSONObject().put("id",id).put("title","Old").put("at",System.currentTimeMillis()-60000).put("delivered",false)).toString());
        Reminders.restore(c);assertTrue(Reminders.list(c).getJSONObject(0).getBoolean("delivered"));Reminders.snooze(c,id,30);assertTrue(Reminders.list(c).getJSONObject(0).has("snoozeAt"));
    }
    @Test public void weeklyValidationCannotOverwriteExistingReminder()throws Exception {
        String id=UUID.randomUUID().toString();Reminders.schedule(c,input(id,"none"));
        try{Reminders.schedule(c,input(id,"weekly"));fail("Missing days accepted");}catch(IllegalArgumentException expected){}
        assertEquals("none",Reminders.list(c).getJSONObject(0).getString("repeat"));
        try{Reminders.schedule(c,input(id,"weekly").put("days",new JSONArray().put(8)));fail("Invalid day accepted");}catch(IllegalArgumentException expected){}
    }
}
