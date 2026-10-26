package ir.marketsignalist.phoenix;
import org.junit.Test;
import static org.junit.Assert.*;
@org.junit.runner.RunWith(org.robolectric.RobolectricTestRunner.class)
@org.robolectric.annotation.Config(sdk=28)
public class TrustedNavigationTest {
    @Test public void restrictBridgeToTheExactHttpsOrigin(){
        assertTrue(TrustedNavigation.trusted("https://marketsignalist.ir/"));
        assertTrue(TrustedNavigation.trusted("https://marketsignalist.ir:443/phoenix"));
        for(String bad:new String[]{"http://marketsignalist.ir/","https://marketsignalist.ir.evil.example/","https://evil.example@marketsignalist.ir/","https://marketsignalist.ir:444/","https://appassets.androidplatform.net/assets/preview/index.html","file:///data/local/file","javascript:alert(1)","https://marketsignalist.ir%2eevil.example/","https://evil.example/"})assertFalse(bad,TrustedNavigation.trusted(bad));
    }
    @Test public void notificationRoutesCannotOpenArbitraryWebPages(){assertEquals("/phoenix",TrustedNavigation.route("/phoenix"));assertEquals("/",TrustedNavigation.route("https://evil.example"));assertEquals("/",TrustedNavigation.route("//evil.example"));assertEquals("/",TrustedNavigation.route(null));}
    @Test public void eventLinksAreRestrictedAndPreserveTheEvent(){
        assertEquals("/notifications?eventId=one%3ATarget",TrustedNavigation.route("/notifications?eventId=one%3ATarget"));
        assertTrue(TrustedNavigation.phoenixPage("https://marketsignalist.ir/notifications?eventId=one%3ATarget"));
        for(String route:new String[]{"//evil.example/notifications","/notifications?eventId=one&next=https://evil.example","/notifications?eventId=%2F%2Fevil.example","/notifications?eventId=bad%"})assertEquals("/",TrustedNavigation.route(route));
    }
    @Test public void standaloneAllowsOnlyPhoenixAndLoginPages(){
        for(String path:new String[]{"/phoenix","/login","/analysis","/analysis/signals","/analysis/coins"})assertTrue(TrustedNavigation.phoenixPage("https://marketsignalist.ir"+path));
        for(String path:new String[]{"/","/index.html","/?view=tasks","/personal","/settings"})assertFalse(TrustedNavigation.phoenixPage("https://marketsignalist.ir"+path));
        assertFalse(TrustedNavigation.phoenixPage("https://evil.example/phoenix"));
    }
    @Test public void coldAndWarmNotificationIntentsRetainEventRoute()throws Exception{
        MainActivity activity=org.robolectric.Robolectric.buildActivity(MainActivity.class).get();
        java.lang.reflect.Method read=MainActivity.class.getDeclaredMethod("readIntent",android.content.Intent.class);read.setAccessible(true);
        java.lang.reflect.Field route=MainActivity.class.getDeclaredField("requestedRoute");route.setAccessible(true);
        read.invoke(activity,new android.content.Intent("phoenix.open").putExtra("route","/notifications?eventId=one%3ATarget"));
        assertEquals("/notifications?eventId=one%3ATarget",route.get(activity));
        read.invoke(activity,new android.content.Intent("phoenix.open").putExtra("route","/notifications?eventId=two%3ARiskFree"));
        assertEquals("/notifications?eventId=two%3ARiskFree",route.get(activity));
        read.invoke(activity,new android.content.Intent("untrusted").putExtra("route","/notifications"));assertEquals("/phoenix",route.get(activity));
    }

}
