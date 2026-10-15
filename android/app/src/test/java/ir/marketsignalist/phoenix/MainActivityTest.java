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
    @Test public void verifiedUnlockStartsRealPageBehindLoadingCover()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();
        set(app,"resumed",true);call(app,"verified");
        assertEquals("https://marketsignalist.ir/",shadowOf((WebView)get(app,"browser")).getLastLoadedUrl());
        assertEquals(View.GONE,((View)get(app,"cover")).getVisibility());
        assertEquals(View.VISIBLE,((View)get(app,"browser")).getVisibility());
        assertEquals(View.VISIBLE,((View)get(app,"pageCover")).getVisibility());
        assertEquals(View.GONE,((View)get(app,"retry")).getVisibility());
    }
    @Test public void authResultBeforeResumeIsAppliedOnResume()throws Exception{
        ActivityController<MainActivity> controller=Robolectric.buildActivity(MainActivity.class).create();MainActivity app=controller.get();
        call(app,"verified");assertFalse((Boolean)get(app,"unlocked"));assertTrue((Boolean)get(app,"credentialVerified"));
        controller.start().resume();assertTrue((Boolean)get(app,"unlocked"));assertFalse((Boolean)get(app,"credentialVerified"));
        controller.pause();assertFalse((Boolean)get(app,"unlocked"));assertEquals(View.VISIBLE,((View)get(app,"cover")).getVisibility());
        assertEquals(View.GONE,((View)get(app,"pageCover")).getVisibility());
    }
    @Test public void timeoutIsVisibleAndRetryLoadsAgainWithoutRemovingLock()throws Exception{
        MainActivity app=Robolectric.buildActivity(MainActivity.class).create().get();set(app,"resumed",true);call(app,"verified");
        ((Runnable)get(app,"loadTimeout")).run();
        assertTrue((Boolean)get(app,"pageFailed"));assertEquals(View.VISIBLE,((View)get(app,"retry")).getVisibility());
        assertTrue(((TextView)get(app,"pageStatus")).getText().toString().contains("زمان اتصال"));
        ((Button)get(app,"retry")).performClick();assertFalse((Boolean)get(app,"pageFailed"));assertTrue((Boolean)get(app,"pageLoading"));
        call(app,"showLock");((Button)get(app,"retry")).performClick();assertFalse((Boolean)get(app,"unlocked"));
        assertEquals(View.INVISIBLE,((View)get(app,"browser")).getVisibility());
    }
}
