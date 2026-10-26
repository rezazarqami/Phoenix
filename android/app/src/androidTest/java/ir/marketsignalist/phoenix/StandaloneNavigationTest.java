package ir.marketsignalist.phoenix;

import android.webkit.*;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import java.io.ByteArrayInputStream;
import java.nio.charset.StandardCharsets;
import java.lang.reflect.Field;
import java.util.concurrent.atomic.*;
import org.junit.*;
import org.junit.runner.RunWith;
import static org.junit.Assert.*;

@RunWith(AndroidJUnit4.class)
public class StandaloneNavigationTest {
    private static WebView browser(MainActivity app)throws Exception{Field f=MainActivity.class.getDeclaredField("browser");f.setAccessible(true);return (WebView)f.get(app);}
    private static WebResourceResponse response(String mime,String text){return new WebResourceResponse(mime,"UTF-8",new ByteArrayInputStream(text.getBytes(StandardCharsets.UTF_8)));}
    private static void waitJs(ActivityScenario<MainActivity> scenario,String script,String expected)throws Exception{
        AtomicReference<String> value=new AtomicReference<>("");long until=System.currentTimeMillis()+10000;
        while(System.currentTimeMillis()<until&&!expected.equals(value.get())){
            scenario.onActivity(app->{try{browser(app).evaluateJavascript(script,value::set);}catch(Exception e){throw new RuntimeException(e);}});Thread.sleep(100);
        }
        assertEquals(script,expected,value.get());
    }
    @Test public void adminLoginAndOrbitLinksStayInsidePhoenix()throws Exception{
        Assume.assumeTrue(BuildConfig.PHOENIX_ONLY);
        AtomicInteger orbitPages=new AtomicInteger();
        try(ActivityScenario<MainActivity> scenario=ActivityScenario.launch(MainActivity.class)){
            scenario.onActivity(app->{try{
                WebView web=browser(app);web.stopLoading();WebViewClient original=web.getWebViewClient();
                web.setWebViewClient(new WebViewClient(){
                    @Override public WebResourceResponse shouldInterceptRequest(WebView v,WebResourceRequest r){
                        String path=r.getUrl().getPath();
                        if(path.equals("/api/auth/login"))return response("application/json","{\"isAdmin\":true}");
                        if(path.equals("/phoenix"))return response("text/html","<html><head></head><body><h1 id='phoenix'>PHOENIX</h1><a id='orbit' href='/'>بازگشت به مدار</a></body></html>");
                        if(path.equals("/")||path.equals("/index.html")){orbitPages.incrementAndGet();return response("text/html","<h1 id='personal'>ORBIT</h1>");}
                        return original.shouldInterceptRequest(v,r);
                    }
                    @Override public boolean shouldOverrideUrlLoading(WebView v,WebResourceRequest r){return original.shouldOverrideUrlLoading(v,r);}
                    @Override public void onPageStarted(WebView v,String u,android.graphics.Bitmap i){original.onPageStarted(v,u,i);}
                    @Override public void onPageFinished(WebView v,String u){original.onPageFinished(v,u);}
                    @Override public void onPageCommitVisible(WebView v,String u){original.onPageCommitVisible(v,u);}
                });
                web.loadUrl("https://marketsignalist.ir/login");
            }catch(Exception e){throw new RuntimeException(e);}});
            waitJs(scenario,"document.title","\"ورود به فونیکس\"");
            scenario.onActivity(app->{try{browser(app).evaluateJavascript("document.querySelector('#username').value='fixture';document.querySelector('#password').value='fixture';document.querySelector('#loginForm').requestSubmit()",null);}catch(Exception e){throw new RuntimeException(e);}});
            waitJs(scenario,"location.pathname+'|'+!!document.getElementById('phoenix')","\"/phoenix|true\"");
            waitJs(scenario,"getComputedStyle(document.getElementById('orbit')).display","\"none\"");
            for(String destination:new String[]{"/","/index.html","/?view=accounting"}){
                scenario.onActivity(app->{try{browser(app).evaluateJavascript("location.href="+org.json.JSONObject.quote(destination),null);}catch(Exception e){throw new RuntimeException(e);}});
                Thread.sleep(500);
                waitJs(scenario,"location.pathname+'|'+!!document.getElementById('personal')","\"/phoenix|false\"");
            }
            assertEquals("Standalone app loaded Orbit HTML",0,orbitPages.get());
        }
    }
}
