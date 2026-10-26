package ir.marketsignalist.phoenix;

import android.app.*;
import android.content.*;
import android.webkit.CookieManager;
import androidx.lifecycle.Lifecycle;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import javax.net.ssl.HttpsURLConnection;
import java.net.URL;
import java.io.*;
import java.security.cert.Certificate;
import java.time.Instant;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import org.junit.*;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
public class DirectAlertBackgroundTest {
    @Test public void nativeServicePostsSignalWithActivityInBackground()throws Exception{
        Context context=InstrumentationRegistry.getInstrumentation().getTargetContext();
        String id="background:"+UUID.randomUUID()+":Target";
        PhoenixAlertService.ConnectionFactory original=PhoenixAlertService.connections;
        PhoenixAlertService.disable(context);
        PhoenixAlertService.connections=url->{
            if(!url.getPath().equals("/api/mobile/feed"))throw new IOException("Unexpected endpoint");
            boolean first=url.getQuery().contains("after=0&");
            String json="{\"account\":\"fixture\",\"cursor\":1,\"events\":"+(first?
                "[{\"id\":\""+id+"\",\"kind\":\"Target\",\"symbol\":\"BTCUSDT\",\"at\":\""+Instant.now()+"\"}]":"[]")+"}";
            return new FakeConnection(url,json);
        };
        try(ActivityScenario<MainActivity> scenario=ActivityScenario.launch(MainActivity.class)){
            try(InputStream permission=new android.os.ParcelFileDescriptor.AutoCloseInputStream(InstrumentationRegistry.getInstrumentation().getUiAutomation().executeShellCommand("pm grant "+context.getPackageName()+" android.permission.POST_NOTIFICATIONS"))){byte[] buffer=new byte[128];while(permission.read(buffer)!=-1){}}
            CountDownLatch cookie=new CountDownLatch(1);
            scenario.onActivity(app->CookieManager.getInstance().setCookie(PhoenixAlertService.HOME,"phoenix-test=fixture; Secure; Path=/",done->cookie.countDown()));
            assertTrue(cookie.await(5,TimeUnit.SECONDS));
            scenario.moveToState(Lifecycle.State.CREATED);
            // Allow network work only after the Activity has moved out of the foreground.
            java.util.concurrent.atomic.AtomicBoolean release=new java.util.concurrent.atomic.AtomicBoolean(false);
            PhoenixAlertService.ConnectionFactory fake=PhoenixAlertService.connections;
            PhoenixAlertService.connections=url->{while(!release.get())try{Thread.sleep(20);}catch(InterruptedException e){throw new IOException(e);}return fake.open(url);};
            scenario.moveToState(Lifecycle.State.RESUMED);
            scenario.onActivity(app->{PhoenixAlertService.prefs(app).edit().putString("account","fixture").putLong("cursor",0).putBoolean("enabled",true).commit();PhoenixAlertService.start(app);});
            scenario.moveToState(Lifecycle.State.CREATED);release.set(true);
            NotificationManager manager=context.getSystemService(NotificationManager.class);boolean found=false;
            long until=System.currentTimeMillis()+10000;
            while(System.currentTimeMillis()<until&&(!found||PhoenixAlertService.prefs(context).getLong("cursor",0)!=1)){for(android.service.notification.StatusBarNotification item:manager.getActiveNotifications())if(id.equals(item.getTag())){String text=String.valueOf(item.getNotification().extras.getCharSequence(Notification.EXTRA_TEXT));assertTrue(text.contains("BTCUSDT"));assertTrue(text.contains("تارگت"));found=true;}if(!found||PhoenixAlertService.prefs(context).getLong("cursor",0)!=1)Thread.sleep(100);}
            assertTrue("Foreground signal service did not notify while Activity was backgrounded",found);
            assertEquals(1,PhoenixAlertService.prefs(context).getLong("cursor",0));
        }finally{PhoenixAlertService.disable(context);PhoenixAlertService.connections=original;CookieManager.getInstance().removeAllCookies(null);context.getSystemService(NotificationManager.class).cancel(id,1);}
    }
    private static final class FakeConnection extends HttpsURLConnection {
        private final byte[] json;
        FakeConnection(URL url,String json){super(url);this.json=json.getBytes(java.nio.charset.StandardCharsets.UTF_8);}
        public void disconnect(){}public boolean usingProxy(){return false;}public void connect(){}
        public int getResponseCode(){return 200;}public InputStream getInputStream(){return new ByteArrayInputStream(json);}
        public String getCipherSuite(){return "fixture";}public Certificate[] getLocalCertificates(){return null;}public Certificate[] getServerCertificates(){return new Certificate[0];}
    }
}
