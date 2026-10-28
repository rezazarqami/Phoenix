package ir.marketsignalist.phoenix;

import android.view.View;
import android.webkit.WebView;
import android.widget.Button;
import android.widget.TextView;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.annotation.Config;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28)
public class MainActivityTest {
    private Object get(MainActivity app,String name)throws Exception{Field f=MainActivity.class.getDeclaredField(name);f.setAccessible(true);return f.get(app);}
    private void set(MainActivity app,String name,Object value)throws Exception{Field f=MainActivity.class.getDeclaredField(name);f.setAccessible(true);f.set(app,value);}
    private void call(MainActivity app,String name)throws Exception{Method m=MainActivity.class.getDeclaredMethod(name);m.setAccessible(true);m.invoke(app);}
    @Test public void launcherReturnKeepsTheCurrentPageAndDoesNotNavigate()throws Exception{
        ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).create().start().resume();
        MainActivity app=controller.get();set(app,"pageLoading",false);set(app,"hasRenderedPage",true);
        long before=(Long)get(app,"navigation");
        controller.pause().stop().restart().start().resume();
        app.onNewIntent(new android.content.Intent(android.content.Intent.ACTION_MAIN).addCategory(android.content.Intent.CATEGORY_LAUNCHER));
        assertEquals(before,((Long)get(app,"navigation")).longValue());
        assertNull(get(app,"requestedRoute"));assertFalse((Boolean)get(app,"pageLoading"));
        assertEquals(View.GONE,((View)get(app,"pageCover")).getVisibility());
    }
    @Test public void initialLoadingShowsOnlyLogoAndCyclingEnglishDots()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();set(app,"resumed",true);call(app,"renderPageState");
        assertEquals(View.VISIBLE,((View)get(app,"loadingLogo")).getVisibility());
        android.graphics.drawable.BitmapDrawable logo=(android.graphics.drawable.BitmapDrawable)((android.widget.ImageView)get(app,"loadingLogo")).getDrawable();
        assertTrue("Splash artwork must not expand into a huge density-scaled bitmap",logo.getBitmap().getWidth()<=768);
        assertEquals(View.GONE,((View)get(app,"pageStatus")).getVisibility());
        assertEquals(View.GONE,((View)get(app,"retry")).getVisibility());
        assertEquals("Loading",((TextView)get(app,"loadingLabel")).getText().toString());
        for(String expected:new String[]{"Loading.","Loading..","Loading...","Loading"}){
            ((Runnable)get(app,"loadingTick")).run();assertEquals(expected,((TextView)get(app,"loadingLabel")).getText().toString());
        }
    }
    @Test public void navigationAfterFirstPaintKeepsTheDocumentVisible()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();
        set(app,"hasRenderedPage",true);call(app,"beginLoading");
        assertEquals(View.GONE,((View)get(app,"pageCover")).getVisibility());
        assertEquals(View.VISIBLE,((View)get(app,"browser")).getVisibility());
    }
    @Test public void launcherUsesNewBundledArtworkAndDarkSystemBars(){
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();
        assertEquals(R.mipmap.phoenix_launcher_v20,app.getApplicationInfo().icon);
        assertEquals(android.graphics.Color.rgb(2,13,35),app.getWindow().getStatusBarColor());
        assertEquals(0,app.getWindow().getDecorView().getSystemUiVisibility() &
            (View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR | View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR));
    }
    @Test public void verifiedUnlockStartsRealPageBehindLoadingCover()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();
        set(app,"resumed",true);call(app,"unlock");
        assertEquals("https://marketsignalist.ir"+(BuildConfig.PHOENIX_ONLY?"/phoenix":"/"),shadowOf((WebView)get(app,"browser")).getLastLoadedUrl());
        assertEquals(0,app.getWindow().getAttributes().flags & android.view.WindowManager.LayoutParams.FLAG_SECURE);
        assertEquals(View.VISIBLE,((View)get(app,"browser")).getVisibility());
        assertEquals(View.VISIBLE,((View)get(app,"pageCover")).getVisibility());
        assertEquals(View.GONE,((View)get(app,"retry")).getVisibility());
    }
    @Test public void backgroundAndReopenNeverRequireBiometrics()throws Exception{
        ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).create().start().resume();MainActivity app=controller.get();
        assertTrue((Boolean)get(app,"unlocked"));
        controller.pause().stop().restart().start().resume();
        assertTrue((Boolean)get(app,"unlocked"));assertEquals(View.VISIBLE,((View)get(app,"browser")).getVisibility());
        assertEquals(0,app.getWindow().getAttributes().flags & android.view.WindowManager.LayoutParams.FLAG_SECURE);
    }
    @Test public void resumeClearsScreenshotRestriction()throws Exception{
        ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).create().start().resume();
        MainActivity app=controller.get();
        controller.pause();
        app.getWindow().addFlags(android.view.WindowManager.LayoutParams.FLAG_SECURE);
        controller.resume();
        assertEquals(0,app.getWindow().getAttributes().flags & android.view.WindowManager.LayoutParams.FLAG_SECURE);
    }
    @Test public void finishedDocumentDoesNotWaitForVisualCallback()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();set(app,"resumed",true);call(app,"unlock");
        WebView web=(WebView)get(app,"browser");web.getWebViewClient().onPageFinished(web,"https://marketsignalist.ir"+(BuildConfig.PHOENIX_ONLY?"/phoenix":"/"));
        assertFalse((Boolean)get(app,"pageLoading"));assertEquals(View.GONE,((View)get(app,"pageCover")).getVisibility());
        ((Runnable)get(app,"loadTimeout")).run();assertFalse((Boolean)get(app,"pageFailed"));
    }
    @Test public void committedDocumentIsShownBeforeAllResourcesFinish()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();set(app,"resumed",true);call(app,"unlock");
        WebView web=(WebView)get(app,"browser");web.getWebViewClient().onPageCommitVisible(web,"https://marketsignalist.ir"+(BuildConfig.PHOENIX_ONLY?"/phoenix":"/"));
        assertFalse((Boolean)get(app,"pageLoading"));assertEquals(View.GONE,((View)get(app,"pageCover")).getVisibility());
    }
    @Test public void failureCannotBeOverwrittenByFinishCallback()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();set(app,"resumed",true);call(app,"unlock");
        Method error=MainActivity.class.getDeclaredMethod("pageError",String.class);error.setAccessible(true);error.invoke(app,"HTTP 403");
        WebView web=(WebView)get(app,"browser");web.getWebViewClient().onPageFinished(web,"https://marketsignalist.ir"+(BuildConfig.PHOENIX_ONLY?"/phoenix":"/"));
        web.getWebViewClient().onPageCommitVisible(web,"https://marketsignalist.ir"+(BuildConfig.PHOENIX_ONLY?"/phoenix":"/"));
        assertTrue((Boolean)get(app,"pageFailed"));assertEquals(View.VISIBLE,((View)get(app,"retry")).getVisibility());
    }
    @Test public void timeoutIsVisibleAndRetryLoadsAgainWithoutRemovingLock()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();set(app,"resumed",true);call(app,"unlock");
        set(app,"checkingConnection",true); // Keep the timeout test independent of external networking.
        ((Runnable)get(app,"loadTimeout")).run();
        assertTrue((Boolean)get(app,"pageFailed"));assertEquals(View.VISIBLE,((View)get(app,"retry")).getVisibility());
        assertTrue(((TextView)get(app,"pageStatus")).getText().toString().contains("باز شدن صفحه"));
        ((Button)get(app,"retry")).performClick();assertFalse((Boolean)get(app,"pageFailed"));assertTrue((Boolean)get(app,"pageLoading"));
        assertTrue((Boolean)get(app,"unlocked"));
        assertEquals(View.VISIBLE,((View)get(app,"browser")).getVisibility());
    }
}
