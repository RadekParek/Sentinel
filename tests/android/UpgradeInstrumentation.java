package com.sentinel.app;
import android.app.Instrumentation;
import android.content.Intent;
import android.content.ComponentName;
import android.os.Bundle;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.io.File;
import org.json.*;

public class UpgradeInstrumentation extends Instrumentation {
 @Override public void onCreate(Bundle arguments){super.onCreate(arguments);start();}
 @Override public void onStart(){JSONObject checks=new JSONObject();Bundle bundle=new Bundle();
  try{
   MainActivity activity=(MainActivity)startActivitySync(new Intent().setComponent(new ComponentName("com.sentinel.app","com.sentinel.app.MainActivity")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK));waitForIdleSync();
   java.lang.reflect.Field backendField=MainActivity.class.getDeclaredField("backend");backendField.setAccessible(true);NativeBackend b=(NativeBackend)backendField.get(activity);String key="synthetic-android-test-key-only-1234";
   JSONObject saved=(JSONObject)b.handle("saveConnection",new JSONObject().put("providerId","cursor").put("adapter","manual").put("name","Instrumentation fixture").put("apiKey",key).put("walletGroup","personal").put("manualBalance",12.5));JSONArray cs=saved.getJSONArray("connections");String id="";for(int i=0;i<cs.length();i++)if("Instrumentation fixture".equals(cs.getJSONObject(i).optString("name")))id=cs.getJSONObject(i).getString("id");
   checks.put("publicStateMasksKey",!saved.toString().contains(key)&&!id.isEmpty());
   byte[] bytes=Files.readAllBytes(new File(activity.getFilesDir(),"vault.bin").toPath());checks.put("keystoreEncryptsAtRest",!new String(bytes,StandardCharsets.UTF_8).contains(key));
   JSONObject reopened=(JSONObject)new NativeBackend(activity).handle("getState",new JSONObject());boolean retained=false;JSONArray loaded=reopened.getJSONArray("connections");for(int i=0;i<loaded.length();i++)if(id.equals(loaded.getJSONObject(i).optString("id")))retained=loaded.getJSONObject(i).optBoolean("hasKey")&&"personal".equals(loaded.getJSONObject(i).optString("walletGroup"));checks.put("keystoreVaultReopens",retained);
   b.handle("saveSnapshots",new JSONObject().put("snapshots",new JSONObject().put(id,new JSONObject().put("status","manual").put("balanceUsd",12.5).put("source","Instrumentation report").put("tokens",120).put("apiKey",key).put("prompt","NEVER_KEEP_PROMPT"))));JSONObject log=(JSONObject)b.handle("getState",new JSONObject());String logged=log.getJSONArray("activityLog").toString();checks.put("reportLogKeepsOnlySafeFields",logged.contains("12.5")&&!logged.contains(key)&&!logged.contains("NEVER_KEEP_PROMPT"));
   b.handle("clearKey",new JSONObject().put("id",id));JSONObject cleared=(JSONObject)new NativeBackend(activity).handle("getState",new JSONObject());boolean noKey=false;JSONArray clearedCs=cleared.getJSONArray("connections");for(int i=0;i<clearedCs.length();i++)if(id.equals(clearedCs.getJSONObject(i).optString("id")))noKey=!clearedCs.getJSONObject(i).optBoolean("hasKey");checks.put("clearKeyPersists",noKey);
   b.handle("clearActivityLog",new JSONObject());checks.put("clearReportLogPersists",((JSONObject)new NativeBackend(activity).handle("getState",new JSONObject())).getJSONArray("activityLog").length()==0);
   checks.put("windowsDiscoveryHonestlyUnavailable",!((JSONObject)b.handle("scanConnections",new JSONObject())).getBoolean("available"));
   b.handle("saveSettings",new JSONObject().put("refreshSeconds",0).put("theme","dark"));checks.put("appearancePersistsNatively","dark".equals(((JSONObject)new NativeBackend(activity).handle("getState",new JSONObject())).getJSONObject("settings").optString("theme")));b.handle("saveSettings",new JSONObject().put("refreshSeconds",300).put("theme","light"));
   b.handle("removeConnection",new JSONObject().put("id",id));
   java.lang.reflect.Field webField=MainActivity.class.getDeclaredField("web");webField.setAccessible(true);android.webkit.WebView web=(android.webkit.WebView)webField.get(activity);java.util.concurrent.CountDownLatch ready=new java.util.concurrent.CountDownLatch(1);final String[] diagnostics=new String[1];Thread.sleep(1000);activity.runOnUiThread(()->web.evaluateJavascript("JSON.stringify({navigation:document.querySelectorAll('.nav-item').length,overflow:document.documentElement.scrollWidth>innerWidth,logos:[...document.images].map(i=>({src:i.getAttribute('src'),loaded:i.naturalWidth>0}))})",value->{diagnostics[0]=value;ready.countDown();}));if(!ready.await(5,java.util.concurrent.TimeUnit.SECONDS))throw new Exception("WebView diagnostics timed out");JSONObject view=new JSONObject(new JSONArray("["+diagnostics[0]+"]").getString(0));checks.put("nativeWebViewNavigationRenders",view.getInt("navigation")==5);checks.put("nativeWebViewHasNoHorizontalOverflow",!view.getBoolean("overflow"));boolean logos=true;JSONArray images=view.getJSONArray("logos");for(int i=0;i<images.length();i++)logos&=images.getJSONObject(i).getBoolean("loaded");checks.put("bundledLogoImagesRender",logos);bundle.putString("view",view.toString());
   boolean passed=true;java.util.Iterator<String> it=checks.keys();while(it.hasNext())passed&=checks.getBoolean(it.next());bundle.putString("report",new JSONObject().put("passed",passed).put("checks",checks).toString());finish(passed?0:1,bundle);
  }catch(Exception e){bundle.putString("report",new JSONObject().toString());bundle.putString("error",e.getClass().getSimpleName()+": "+e.getMessage());finish(1,bundle);}
 }
}
