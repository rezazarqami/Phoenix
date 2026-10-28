package ir.marketsignalist.phoenix;

import android.content.Intent;
import android.webkit.*;
import androidx.lifecycle.Lifecycle;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.lang.reflect.Field;
import java.util.concurrent.atomic.*;
import org.junit.Test;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
public class SessionContinuityTest {
    private static Object field(MainActivity app,String name)throws Exception{Field f=MainActivity.class.getDeclaredField(name);f.setAccessible(true);return f.get(app);}
    private static void waitJs(ActivityScenario<MainActivity> scenario,String script,String expected)throws Exception{
        AtomicReference<String> value=new AtomicReference<>("");long until=System.currentTimeMillis()+10000;
        while(System.currentTimeMillis()<until&&!expected.equals(value.get())){
            scenario.onActivity(app->{try{((WebView)field(app,"browser")).evaluateJavascript(script,value::set);}catch(Exception e){throw new RuntimeException(e);}});Thread.sleep(100);
        }
        assertEquals(script,expected,value.get());
    }
    @Test public void returningFromAnotherAppOrLauncherRetainsDomInputAndScroll()throws Exception{
        AtomicInteger documents=new AtomicInteger();
        try(ActivityScenario<MainActivity> scenario=ActivityScenario.launch(MainActivity.class)){
            scenario.onActivity(app->{try{
                WebView web=(WebView)field(app,"browser");web.stopLoading();WebViewClient original=web.getWebViewClient();
                web.setWebViewClient(new WebViewClient(){
                    @Override public WebResourceResponse shouldInterceptRequest(WebView v,WebResourceRequest request){
                        if(request.isForMainFrame()){
                            documents.incrementAndGet();String html="<!doctype html><html><body style='height:4000px'><input id='draft'><script>window.documentToken='loaded';</script></body></html>";
                            return new WebResourceResponse("text/html","UTF-8",new ByteArrayInputStream(html.getBytes(StandardCharsets.UTF_8)));
                        }
                        return original.shouldInterceptRequest(v,request);
                    }
                    @Override public void onPageStarted(WebView v,String u,android.graphics.Bitmap i){original.onPageStarted(v,u,i);}
                    @Override public void onPageCommitVisible(WebView v,String u){original.onPageCommitVisible(v,u);}
                    @Override public void onPageFinished(WebView v,String u){original.onPageFinished(v,u);}
                });
                web.loadUrl("https://marketsignalist.ir/phoenix");
            }catch(Exception e){throw new RuntimeException(e);}});
            waitJs(scenario,"window.documentToken","\"loaded\"");
            scenario.onActivity(app->{try{((WebView)field(app,"browser")).evaluateJavascript("documentToken='retained';document.getElementById('draft').value='unsent';scrollTo(0,500)",null);}catch(Exception e){throw new RuntimeException(e);}});
            waitJs(scenario,"documentToken+'|'+document.getElementById('draft').value+'|'+scrollY","\"retained|unsent|500\"");
            int before=documents.get();
            scenario.moveToState(Lifecycle.State.CREATED);scenario.moveToState(Lifecycle.State.RESUMED);
            scenario.onActivity(app->app.onNewIntent(new Intent(app,MainActivity.class).setAction(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER)));
            waitJs(scenario,"documentToken+'|'+document.getElementById('draft').value+'|'+scrollY","\"retained|unsent|500\"");
            assertEquals("Resume must not request a new document",before,documents.get());
            scenario.onActivity(app->{try{assertEquals(android.view.View.GONE,((android.view.View)field(app,"pageCover")).getVisibility());}catch(Exception e){throw new RuntimeException(e);}});
        }
    }
}
