package ir.marketsignalist.phoenix;

import android.app.Notification;
import android.app.NotificationManager;
import android.content.Context;
import com.google.firebase.messaging.RemoteMessage;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28)
public class PushReceptionTest {
    private RemoteMessage message(String kind){return new RemoteMessage.Builder("fixture").addData("kind",kind).addData("eventId","signal-id:Target").addData("eventKind","Target").build();}
    @Test public void receivedPushIsPrivateRoutedThroughActivityAndDeduplicatedAfterDismissal(){
        PhoenixMessagingService service=Robolectric.buildService(PhoenixMessagingService.class).create().get();
        service.getSharedPreferences("orbit-device",Context.MODE_PRIVATE).edit().putBoolean("push-enabled",true).commit();
        service.onMessageReceived(message("signal"));
        NotificationManager manager=service.getSystemService(NotificationManager.class);
        assertEquals(1,shadowOf(manager).getAllNotifications().size());
        Notification notice=shadowOf(manager).getAllNotifications().get(0);
        assertEquals(Notification.VISIBILITY_PRIVATE,notice.visibility);
        assertNotNull(notice.publicVersion);
        assertEquals("/phoenix",shadowOf(notice.contentIntent).getSavedIntent().getStringExtra("route"));
        assertEquals(MainActivity.class.getName(),shadowOf(notice.contentIntent).getSavedIntent().getComponent().getClassName());
        manager.cancelAll();service.onMessageReceived(message("signal"));
        assertEquals("Retry must not resurface a dismissed notification",0,shadowOf(manager).getAllNotifications().size());
    }
    @Test public void disabledOrUnrecognizedPushCannotDisplayNotification(){
        PhoenixMessagingService service=Robolectric.buildService(PhoenixMessagingService.class).create().get();
        NotificationManager manager=service.getSystemService(NotificationManager.class);
        service.onMessageReceived(message("signal"));assertEquals(0,shadowOf(manager).getAllNotifications().size());
        service.getSharedPreferences("orbit-device",Context.MODE_PRIVATE).edit().putBoolean("push-enabled",true).commit();
        service.onMessageReceived(message("foreign-command"));assertEquals(0,shadowOf(manager).getAllNotifications().size());
    }
}
