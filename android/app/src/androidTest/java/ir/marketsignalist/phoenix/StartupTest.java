package ir.marketsignalist.phoenix;

import android.app.Instrumentation;
import android.content.IntentFilter;
import android.provider.Settings;
import android.view.View;
import android.webkit.*;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import androidx.test.platform.app.InstrumentationRegistry;
import org.junit.Test;
import org.junit.runner.RunWith;
import java.io.ByteArrayInputStream;
import java.lang.reflect.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.atomic.AtomicReference;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
public class StartupTest {
    private static Object get(Object app,String name)throws Exception{Field f=MainActivity.class.getDeclaredField(name);f.setAccessible(true);return f.get(app);}
    private static void call(Object app,String name)throws Exception{Method m=MainActivity.class.getDeclaredMethod(name);m.setAccessible(true);m.invoke(app);}
    @Test public void realWebViewRevealsLoadedPageAfterUnlock()throws Exception{
        Instrumentation instrument=InstrumentationRegistry.getInstrumentation();
        Instrumentation.ActivityMonitor monitor=instrument.addMonitor(new IntentFilter(Settings.ACTION_SECURITY_SETTINGS),null,true);
        AtomicReference<Throwable> failure=new AtomicReference<>();
        try(ActivityScenario<MainActivity> scenario=ActivityScenario.launch(MainActivity.class)){
            scenario.onActivity(app->{try{
                WebView web=(WebView)get(app,"browser");WebViewClient client=web.getWebViewClient();
                web.setWebViewClient(new WebViewClient(){
                    @Override public WebResourceResponse shouldInterceptRequest(WebView view,WebResourceRequest request){
                        return new WebResourceResponse("text/html","UTF-8",new ByteArrayInputStream("<!doctype html><html><body style='background:white;color:black'><h1 id='ready'>Orbit ready</h1></body></html>".getBytes(StandardCharsets.UTF_8)));
                    }
                    @Override public void onPageStarted(WebView v,String url,android.graphics.Bitmap icon){client.onPageStarted(v,url,icon);}
                    @Override public void onPageFinished(WebView v,String url){client.onPageFinished(v,url);}
                    @Override public void onPageCommitVisible(WebView v,String url){client.onPageCommitVisible(v,url);}
                });
                call(app,"verified");
            }catch(Throwable e){failure.set(e);}});
            // Poll the actual compositor/UI state, not a mocked completion callback.
            long deadline=System.currentTimeMillis()+12000;
            AtomicReference<Boolean> shown=new AtomicReference<>(false);
            while(System.currentTimeMillis()<deadline&&!shown.get()&&failure.get()==null){
                scenario.onActivity(app->{try{shown.set(!(Boolean)get(app,"pageLoading")&&!(Boolean)get(app,"pageFailed")&&((View)get(app,"pageCover")).getVisibility()==View.GONE&&((View)get(app,"browser")).getVisibility()==View.VISIBLE);}catch(Throwable e){failure.set(e);}});
                if(!shown.get())Thread.sleep(100);
            }
            if(failure.get()!=null)throw new AssertionError(failure.get());
            assertTrue("Loaded HTML remained behind the native loading cover",shown.get());
            scenario.onActivity(app->{try{call(app,"showLock");assertEquals(View.INVISIBLE,((View)get(app,"browser")).getVisibility());assertEquals(View.VISIBLE,((View)get(app,"cover")).getVisibility());}catch(Throwable e){failure.set(e);}});
            if(failure.get()!=null)throw new AssertionError(failure.get());
        }finally{instrument.removeMonitor(monitor);}
    }
}
