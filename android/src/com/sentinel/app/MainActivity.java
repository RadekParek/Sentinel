package com.sentinel.app;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.view.WindowInsets;
import android.webkit.JavascriptInterface;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.FrameLayout;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONObject;

public final class MainActivity extends Activity {
    private WebView web; private FrameLayout container; private NativeBackend backend; private final ExecutorService executor=Executors.newFixedThreadPool(3);
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved);
        getWindow().setStatusBarColor(Color.rgb(9,13,20));getWindow().setNavigationBarColor(Color.rgb(9,13,20));
        container=new FrameLayout(this);container.setBackgroundColor(Color.rgb(245,246,250));web=new WebView(this);web.setBackgroundColor(Color.rgb(245,246,250));
        container.addView(web,new FrameLayout.LayoutParams(-1,-1));setContentView(container);
        container.setOnApplyWindowInsetsListener((v,insets)->{v.setPadding(insets.getSystemWindowInsetLeft(),insets.getSystemWindowInsetTop(),insets.getSystemWindowInsetRight(),insets.getSystemWindowInsetBottom());return insets.consumeSystemWindowInsets();});
        backend=new NativeBackend(this);
        try{applyAppearance(((JSONObject)backend.handle("getState",new JSONObject())).getJSONObject("settings").optString("theme","light"));}catch(Exception ignored){applyAppearance("light");}
        WebSettings settings=web.getSettings();settings.setJavaScriptEnabled(true);settings.setDomStorageEnabled(false);settings.setAllowFileAccess(false);settings.setAllowContentAccess(false);settings.setAllowFileAccessFromFileURLs(false);settings.setAllowUniversalAccessFromFileURLs(false);settings.setMixedContentMode(WebSettings.MIXED_CONTENT_NEVER_ALLOW);settings.setCacheMode(WebSettings.LOAD_NO_CACHE);
        WebView.setWebContentsDebuggingEnabled(false);
        web.setWebViewClient(new WebViewClient(){
            @Override public boolean shouldOverrideUrlLoading(WebView view,WebResourceRequest request){return navigate(request.getUrl().toString());}
            @Override public boolean shouldOverrideUrlLoading(WebView view,String url){return navigate(url);}
            private boolean navigate(String url){if(url.startsWith("file:///android_asset/web/index.html"))return false;openExternal(url);return true;}
        });
        web.addJavascriptInterface(new Bridge(),"SentinelNative");web.loadUrl("file:///android_asset/web/index.html");
    }
    void openExternal(String value){try{Uri uri=Uri.parse(value);if("https".equals(uri.getScheme())&&uri.getHost()!=null&&uri.getUserInfo()==null)startActivity(new Intent(Intent.ACTION_VIEW,uri));}catch(Exception ignored){}}
    void applyAppearance(String theme){boolean dark="dark".equals(theme);int color=dark?Color.rgb(21,24,32):Color.rgb(245,246,250);getWindow().setStatusBarColor(color);getWindow().setNavigationBarColor(color);getWindow().getDecorView().setSystemUiVisibility(dark?0:View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);getWindow().getDecorView().setBackgroundColor(color);if(container!=null)container.setBackgroundColor(color);if(web!=null)web.setBackgroundColor(color);if(android.os.Build.VERSION.SDK_INT>=30){android.view.WindowInsetsController controller=getWindow().getInsetsController();if(controller!=null)controller.setSystemBarsAppearance(dark?0:android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS,android.view.WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS|android.view.WindowInsetsController.APPEARANCE_LIGHT_NAVIGATION_BARS);}}
    final class Bridge {
        @JavascriptInterface public void postMessage(final String value) {
            if(value.length()>6000000)return;
            executor.execute(()->{
                String id="";JSONObject response=new JSONObject();
                try{JSONObject req=new JSONObject(value);id=req.optString("id");response.put("id",id);response.put("result",backend.handle(req.optString("method"),req.optJSONObject("params")==null?new JSONObject():req.getJSONObject("params")));}
                catch(Exception e){try{response.put("id",id);response.put("error",e instanceof NativeBackend.SafeException?e.getMessage():"Operation failed. Try again.");}catch(Exception ignored){}}
                final String payload=response.toString();runOnUiThread(()->{if(!isFinishing()&&web!=null&&"file:///android_asset/web/index.html".equals(web.getUrl()))web.evaluateJavascript("window.sentinelReceive(JSON.parse("+JSONObject.quote(payload)+"));",null);});
            });
        }
    }
    @Override public void onBackPressed(){web.evaluateJavascript("window.sentinelBack && window.sentinelBack();",null);}
    @Override protected void onDestroy(){executor.shutdownNow();if(web!=null){web.removeJavascriptInterface("SentinelNative");web.destroy();web=null;}super.onDestroy();}
}
