package ir.marketsignalist.phoenix;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.os.Bundle;
import android.view.View;
import android.webkit.CookieManager;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

public final class MainActivity extends Activity {
    private static final String HOME = "https://marketsignalist.ir/";
    private WebView browser;

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setStatusBarColor(Color.rgb(11, 14, 17));
        getWindow().setNavigationBarColor(Color.rgb(11, 14, 17));
        browser = new WebView(this);
        browser.setBackgroundColor(Color.rgb(11, 14, 17));
        browser.setOverScrollMode(View.OVER_SCROLL_NEVER);
        setContentView(browser);
        WebSettings settings = browser.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setUserAgentString(settings.getUserAgentString() + " PhoenixAndroid/1.0");
        settings.setAllowFileAccess(false);
        settings.setAllowContentAccess(false);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);
        CookieManager.getInstance().setAcceptCookie(true);
        browser.setWebViewClient(new WebViewClient() {
            @Override public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                String host = request.getUrl().getHost();
                if ("marketsignalist.ir".equalsIgnoreCase(host) && "https".equalsIgnoreCase(request.getUrl().getScheme())) return false;
                if (request.isForMainFrame()) startActivity(new Intent(Intent.ACTION_VIEW, request.getUrl()));
                return true;
            }
        });
        if (state == null) browser.loadUrl(HOME);
        else browser.restoreState(state);
    }

    @Override protected void onSaveInstanceState(Bundle state) {
        browser.saveState(state);
        super.onSaveInstanceState(state);
    }

    @Override public void onBackPressed() {
        if (browser.canGoBack()) browser.goBack();
        else super.onBackPressed();
    }

    @Override protected void onDestroy() {
        browser.destroy();
        super.onDestroy();
    }
}
