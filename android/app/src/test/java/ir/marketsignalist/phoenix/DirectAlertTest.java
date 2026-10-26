package ir.marketsignalist.phoenix;

import android.app.*;
import android.content.*;
import org.json.*;
import org.junit.*;
import org.junit.runner.RunWith;
import org.robolectric.*;
import org.robolectric.annotation.Config;
import java.time.Instant;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28)
public class DirectAlertTest {
    private Context context;
    @Before public void setup(){context=RuntimeEnvironment.getApplication();context.getSharedPreferences("phoenix-sounds",0).edit().clear().commit();PhoenixNotifications.channels(context);PhoenixAlertService.prefs(context).edit().clear().putBoolean("enabled",true).putString("account","owner").putLong("cursor",10).commit();context.getSharedPreferences("orbit-push-seen",0).edit().clear().commit();context.getSystemService(NotificationManager.class).cancelAll();}
    private JSONObject event(String id,String kind,String exit)throws Exception{return new JSONObject().put("id",id).put("kind",kind).put("symbol","BTCUSDT").put("exitKind",exit).put("at",Instant.now().toString());}
    private JSONObject page(JSONArray events)throws Exception{return new JSONObject().put("account","owner").put("cursor",12).put("events",events);}
    @Test public void actualFeedPostsEachEventOnceAndPersistsCursor()throws Exception{
        JSONObject page=page(new JSONArray().put(event("signal:Opened","Opened","")).put(event("signal:Target","Target","")));
        assertTrue(PhoenixAlertService.consume(context,page));assertEquals(12,PhoenixAlertService.prefs(context).getLong("cursor",0));
        assertEquals(2,context.getSystemService(NotificationManager.class).getActiveNotifications().length);
        assertTrue(PhoenixAlertService.consume(context,page));assertEquals(2,context.getSharedPreferences("orbit-push-seen",0).getAll().size());
        String text=context.getSystemService(NotificationManager.class).getActiveNotifications()[0].getNotification().extras.getCharSequence(Notification.EXTRA_TEXT).toString();assertTrue(text.contains("BTCUSDT"));
    }
    @Test public void wrongAccountAndBackwardCursorNeverPost()throws Exception{
        JSONObject page=page(new JSONArray().put(event("private:Target","Target",""))).put("account","someone-else");
        try{PhoenixAlertService.consume(context,page);fail();}catch(java.io.IOException expected){}
        page.put("account","owner").put("cursor",9);try{PhoenixAlertService.consume(context,page);fail();}catch(java.io.IOException expected){}
        assertEquals(0,context.getSystemService(NotificationManager.class).getActiveNotifications().length);assertEquals(10,PhoenixAlertService.prefs(context).getLong("cursor",0));
    }
    @Test public void riskFreeOrderLabelsAndLateDeliveryAreVisible()throws Exception{
        assertTrue(PhoenixAlertDelivery.deliver(context,"one:RiskFree","RiskFree","ETHUSDT","FirstStepLimit",System.currentTimeMillis()-180000));
        String text=context.getSystemService(NotificationManager.class).getActiveNotifications()[0].getNotification().extras.getCharSequence(Notification.EXTRA_TEXT).toString();assertTrue(text.contains("پلهٔ اول"));assertTrue(text.contains("تأخیر"));
        assertFalse(PhoenixAlertDelivery.deliver(context,"bad","made-up","BTCUSDT",null,0));
        PhoenixAlertService.prefs(context).edit().putBoolean("enabled",false).commit();assertFalse(PhoenixAlertService.consume(context,page(new JSONArray().put(event("two:StopLoss","StopLoss","")))));
        assertEquals(10,PhoenixAlertService.prefs(context).getLong("cursor",0));
    }
    @Test public void excludedEventsAdvanceCursorWithoutPosting()throws Exception{
        assertTrue(PhoenixAlertService.consume(context,page(new JSONArray().put(event("one:Created","Created","")).put(event("two:Cancelled","Cancelled","")))));
        assertEquals(0,context.getSystemService(NotificationManager.class).getActiveNotifications().length);
        assertEquals(12,PhoenixAlertService.prefs(context).getLong("cursor",0));
    }
    @Test public void tonesUseIndependentChannelsAndPhoenixIcon()throws Exception{
        context.getSharedPreferences("phoenix-sounds",0).edit().putString("tone","pulse").commit();
        PhoenixNotifications.channels(context);
        assertTrue(PhoenixAlertDelivery.deliver(context,"tone:Target","Target","BTCUSDT",null,0));
        Notification notification=context.getSystemService(NotificationManager.class).getActiveNotifications()[0].getNotification();
        assertEquals("phoenix-tone-pulse",notification.getChannelId());
        assertEquals(R.drawable.ic_notification_p,notification.getSmallIcon().getResId());
        assertTrue(context.getSystemService(NotificationManager.class).getNotificationChannel("phoenix-tone-pulse").getSound().toString().startsWith("android.resource://"));
    }

}
