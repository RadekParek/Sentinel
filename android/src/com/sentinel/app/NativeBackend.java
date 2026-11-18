package com.sentinel.app;

import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.text.SimpleDateFormat;
import java.time.Instant;
import java.util.*;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import org.json.*;

final class NativeBackend {
    static final class SafeException extends Exception {SafeException(String message){super(message);}}
    private static final int ACTIVITY_LIMIT=500;
    private final MainActivity activity;private final File vault;private JSONObject config,routes;private String vaultError;private String oauthVerifier;private long oauthStarted;
    NativeBackend(MainActivity a){
        activity=a;vault=new File(a.getFilesDir(),"vault.bin");
        try {routes=new JSONObject(read(a.getAssets().open("web/routes.json"),5000000));config=new JSONObject().put("connections",new JSONArray()).put("snapshots",new JSONObject()).put("activityLog",new JSONArray()).put("settings",new JSONObject().put("refreshSeconds",300));
            if(vault.exists())try{byte[] encrypted=bytes(new FileInputStream(vault),6000000);byte[] iv=Arrays.copyOfRange(encrypted,0,12);Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,iv));byte[] clear=cipher.doFinal(Arrays.copyOfRange(encrypted,12,encrypted.length));config=new JSONObject(new String(clear,StandardCharsets.UTF_8));Arrays.fill(clear,(byte)0);}catch(Exception e){vaultError="Your encrypted vault could not be opened. Existing data was preserved. Restore the app's Android Keystore access before saving.";}
            if(config.optJSONArray("activityLog")==null)config.put("activityLog",new JSONArray());
            config.put("activityLog",safeActivityLog(config.getJSONArray("activityLog")));
        } catch(Exception ignored){vaultError="Secure storage could not be initialized.";}
    }
    private SecretKey key()throws Exception {
        KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);String alias="sentinel-vault-v1";
        if(!store.containsAlias(alias)){KeyGenerator generator=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");generator.init(new KeyGenParameterSpec.Builder(alias,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build());generator.generateKey();}
        return (SecretKey)store.getKey(alias,null);
    }
    private void save()throws Exception {
        if(vaultError!=null)throw new SafeException(vaultError);byte[] clear=config.toString().getBytes(StandardCharsets.UTF_8);Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");cipher.init(Cipher.ENCRYPT_MODE,key());byte[] encrypted=cipher.doFinal(clear);Arrays.fill(clear,(byte)0);
        File temp=new File(vault.getPath()+".tmp");try(FileOutputStream out=new FileOutputStream(temp)){out.write(cipher.getIV());out.write(encrypted);out.getFD().sync();}if(!temp.renameTo(vault))throw new SafeException("Could not save the secure vault.");
    }
    private JSONObject unavailable(String product)throws Exception {return new JSONObject().put("status","unavailable").put("source","Windows local activity").put("detail",product+" session logs live on your Windows computer. This phone cannot read them; use Sentinel.exe for local usage, or connect the organization's API for cloud reports.");}
    private JSONObject state()throws Exception {
        JSONArray connections=new JSONArray();JSONArray saved=config.getJSONArray("connections");
        for(int i=0;i<saved.length();i++){JSONObject c=new JSONObject(saved.getJSONObject(i).toString());String secret=c.optString("apiKey");c.remove("apiKey");c.put("hasKey",!secret.isEmpty());c.put("keyHint",secret.length()>=4?"••••"+secret.substring(secret.length()-4):secret.isEmpty()?"No key":"••••");connections.put(c);}
        return new JSONObject().put("platform","android").put("version","1.1.0").put("connections",connections).put("snapshots",new JSONObject(config.getJSONObject("snapshots").toString())).put("activityLog",safeActivityLog(config.optJSONArray("activityLog"))).put("settings",new JSONObject(config.getJSONObject("settings").toString())).put("localUsage",new JSONObject().put("codex",unavailable("Codex")).put("claude",unavailable("Claude Code"))).put("refreshedAt",config.opt("refreshedAt")).put("vaultError",vaultError);
    }
    Object handle(String method,JSONObject p)throws Exception {
        if("request".equals(method))return request(p);
        if("signInOpenRouter".equals(method)){
            byte[] random=new byte[32];new java.security.SecureRandom().nextBytes(random);String verifier=android.util.Base64.encodeToString(random,android.util.Base64.URL_SAFE|android.util.Base64.NO_WRAP|android.util.Base64.NO_PADDING);String challenge=android.util.Base64.encodeToString(java.security.MessageDigest.getInstance("SHA-256").digest(verifier.getBytes(StandardCharsets.US_ASCII)),android.util.Base64.URL_SAFE|android.util.Base64.NO_WRAP|android.util.Base64.NO_PADDING);
            synchronized(this){if(vaultError!=null)throw new SafeException(vaultError);oauthVerifier=verifier;oauthStarted=System.currentTimeMillis();}
            final String url="https://openrouter.ai/auth?code_challenge="+challenge+"&code_challenge_method=S256&key_label=Sentinel&limit=1&expiration=30-days";activity.runOnUiThread(()->activity.openExternal(url));return new JSONObject().put("pendingAuthorization",true);
        }
        if("cancelSignIn".equals(method)){synchronized(this){oauthVerifier=null;}return new JSONObject().put("ok",true);}
        if("finishSignInOpenRouter".equals(method)){
            String verifier; synchronized(this){verifier=oauthVerifier;oauthVerifier=null;}String code=p.optString("code").trim();if(verifier==null||System.currentTimeMillis()-oauthStarted>600000||code.isEmpty()||code.length()>4096||control(code))throw new SafeException("Authorization expired. Start sign-in again.");
            HttpURLConnection request=(HttpURLConnection)new URL("https://openrouter.ai/api/v1/auth/keys").openConnection();request.setInstanceFollowRedirects(false);request.setConnectTimeout(12000);request.setReadTimeout(30000);request.setRequestMethod("POST");request.setDoOutput(true);request.setRequestProperty("Content-Type","application/json");
            try{byte[] data=new JSONObject().put("code",code).put("code_verifier",verifier).put("code_challenge_method","S256").toString().getBytes(StandardCharsets.UTF_8);try(OutputStream out=request.getOutputStream()){out.write(data);}if(request.getResponseCode()!=200)throw new SafeException("OpenRouter rejected the authorization. Start sign-in again.");String secret=new JSONObject(read(request.getInputStream(),65536)).optString("key");if(secret.isEmpty()||secret.length()>4096||control(secret))throw new SafeException("OpenRouter did not return a key.");return handle("saveConnection",new JSONObject().put("providerId","openrouter").put("name","OpenRouter · authorized account").put("adapter","openrouter").put("apiKey",secret));}catch(IOException e){throw new SafeException("Could not reach OpenRouter for authorization.");}finally{request.disconnect();}
        }
        synchronized(this){
            if("getState".equals(method))return state();
            if("scanLocal".equals(method))return state().getJSONObject("localUsage");
            if("scanConnections".equals(method))return new JSONObject().put("available",false).put("discoveries",new JSONArray()).put("detail","Local connection discovery is available in Sentinel.exe on Windows. Android cannot read credentials or sessions from your computer.").put("checkedAt",now());
            if("importLocalConnections".equals(method))return state().put("importResult",new JSONObject().put("connected",0).put("existing",0).put("skipped",0).put("detail","Windows only: use Sentinel.exe to discover and connect local provider credentials. No credentials were imported on Android."));
            if("getOllamaUsage".equals(method))return new JSONObject().put("available",false);
            if("saveConnection".equals(method)){
                if(vaultError!=null)throw new SafeException(vaultError);
                String adapter=p.optString("adapter"),name=p.optString("name").trim(),provider=p.optString("providerId"),secret=p.optString("apiKey"),accountLabel=p.optString("accountLabel").trim(),walletGroup=p.optString("walletGroup").trim();
                if(!routes.has(adapter)||name.isEmpty()||name.length()>120||provider.isEmpty()||provider.length()>120||control(name)||control(provider)||secret.length()>4096||control(secret)||accountLabel.length()>120||walletGroup.length()>120||control(accountLabel)||control(walletGroup))throw new SafeException("Check the connection name, connector, account label, wallet group and key.");
                String base=p.optString("baseUrl");if("ollama".equals(adapter))validateOllama(base);
                String external=p.optString("url");if(!external.isEmpty()){URL link;try{link=new URL(external);}catch(Exception e){throw new SafeException("Use an HTTPS dashboard URL.");}if(!"https".equals(link.getProtocol())||link.getUserInfo()!=null)throw new SafeException("Use an HTTPS dashboard URL.");}
                Double balance=null;if(p.has("manualBalance")&&!p.isNull("manualBalance")){balance=p.optDouble("manualBalance",Double.NaN);if(!Double.isFinite(balance)||Math.abs(balance)>1e12)throw new SafeException("Invalid wallet amount.");}
                JSONArray cs=config.getJSONArray("connections");JSONObject current=null;for(int i=0;i<cs.length();i++)if(cs.getJSONObject(i).optString("id").equals(p.optString("id")))current=cs.getJSONObject(i);
                if(current!=null&&(!current.optString("adapter").equals(adapter)||!current.optString("providerId").equals(provider)))throw new SafeException("Create a new connection to change its provider.");
                if(current==null){if(cs.length()>=100)throw new SafeException("Connection limit reached.");current=new JSONObject().put("id",UUID.randomUUID().toString());cs.put(current);}
                current.put("providerId",provider).put("name",name).put("adapter",adapter);if(p.has("accountLabel"))current.put("accountLabel",accountLabel);if(p.has("walletGroup"))current.put("walletGroup",walletGroup);if(p.has("url"))current.put("url",external);if(p.optBoolean("clearKey"))current.remove("apiKey");else if(!secret.isEmpty())current.put("apiKey",secret);if("ollama".equals(adapter))current.put("baseUrl",base.replaceAll("/+$",""));
                if(p.has("manualBalance")){Double previous=current.isNull("manualBalance")?null:current.optDouble("manualBalance",Double.NaN);boolean changed=previous==null?balance!=null:balance==null||Double.compare(previous,balance)!=0;current.put("manualBalance",balance==null?JSONObject.NULL:balance);if(changed)current.put("manualUpdatedAt",now());}
                config.getJSONObject("snapshots").remove(current.getString("id"));save();return state();
            }
            if("removeConnection".equals(method)){
                if(vaultError!=null)throw new SafeException(vaultError);
                JSONArray cs=config.getJSONArray("connections");for(int i=cs.length()-1;i>=0;i--)if(cs.getJSONObject(i).optString("id").equals(p.optString("id")))cs.remove(i);config.getJSONObject("snapshots").remove(p.optString("id"));save();return state();
            }
            if("clearKey".equals(method)){
                if(vaultError!=null)throw new SafeException(vaultError);
                JSONObject connection=findConnection(p.optString("id"));if(connection==null)throw new SafeException("Connection was removed.");connection.remove("apiKey");config.getJSONObject("snapshots").remove(connection.getString("id"));save();return state();
            }
            if("clearActivityLog".equals(method)){
                if(vaultError!=null)throw new SafeException(vaultError);
                config.put("activityLog",new JSONArray());save();return state();
            }
            if("saveSettings".equals(method)){
                if(vaultError!=null)throw new SafeException(vaultError);
                int seconds=p.optInt("refreshSeconds",300);if(seconds!=0&&seconds!=60&&seconds!=300&&seconds!=900)throw new SafeException("Invalid refresh interval.");config.getJSONObject("settings").put("refreshSeconds",seconds);if(p.has("theme")){String theme=p.optString("theme");if(!"light".equals(theme)&&!"dark".equals(theme))throw new SafeException("Invalid appearance.");config.getJSONObject("settings").put("theme",theme);activity.runOnUiThread(()->activity.applyAppearance(theme));}save();return state();
            }
            if("saveSnapshots".equals(method)){
                if(vaultError!=null)throw new SafeException(vaultError);
                JSONObject snapshots=p.optJSONObject("snapshots");if(snapshots==null||snapshots.toString().length()>5000000)throw new SafeException("Invalid snapshot.");JSONObject filtered=new JSONObject();JSONArray cs=config.getJSONArray("connections"),history=new JSONArray();String at=timestamp(p.opt("refreshedAt"));if(at==null)at=now();
                for(int i=0;i<cs.length();i++){JSONObject connection=cs.getJSONObject(i);String id=connection.getString("id");JSONObject report=snapshots.optJSONObject(id);if(report!=null){filtered.put(id,report);history.put(safeReport(report,connection,at));}}
                JSONArray previous=safeActivityLog(config.optJSONArray("activityLog"));for(int i=0;i<previous.length()&&history.length()<ACTIVITY_LIMIT;i++)history.put(previous.get(i));config.put("snapshots",filtered).put("activityLog",history).put("refreshedAt",at);save();return state();
            }
            if("openExternal".equals(method)){final String url=p.optString("url");activity.runOnUiThread(()->activity.openExternal(url));return new JSONObject().put("ok",true);}
            if("copyKey".equals(method)){
                String found="";JSONArray cs=config.getJSONArray("connections");for(int i=0;i<cs.length();i++)if(cs.getJSONObject(i).optString("id").equals(p.optString("id")))found=cs.getJSONObject(i).optString("apiKey");if(found.isEmpty())throw new SafeException("No key is stored for this connection.");
                final String secret=found;activity.runOnUiThread(()->{android.content.ClipboardManager clipboard=(android.content.ClipboardManager)activity.getSystemService(android.content.Context.CLIPBOARD_SERVICE);android.content.ClipData data=android.content.ClipData.newPlainText("Sentinel API key",secret);android.os.PersistableBundle extras=new android.os.PersistableBundle();extras.putBoolean("android.content.extra.IS_SENSITIVE",true);data.getDescription().setExtras(extras);clipboard.setPrimaryClip(data);new android.os.Handler(android.os.Looper.getMainLooper()).postDelayed(()->{try{if(clipboard.hasPrimaryClip()&&secret.contentEquals(clipboard.getPrimaryClip().getItemAt(0).getText())){if(android.os.Build.VERSION.SDK_INT>=28)clipboard.clearPrimaryClip();else clipboard.setPrimaryClip(android.content.ClipData.newPlainText("", ""));}}catch(Exception ignored){}},60000);});return new JSONObject().put("ok",true);
            }
            throw new SafeException("Unsupported operation.");
        }
    }
    private static boolean control(String s){for(int i=0;i<s.length();i++)if(Character.isISOControl(s.charAt(i)))return true;return false;}
    private static String now(){return Instant.now().toString();}
    private static String timestamp(Object value){try{return Instant.parse(String.valueOf(value)).toString();}catch(Exception e){return null;}}
    private JSONObject findConnection(String id)throws Exception {JSONArray cs=config.getJSONArray("connections");for(int i=0;i<cs.length();i++)if(id.equals(cs.getJSONObject(i).optString("id")))return cs.getJSONObject(i);return null;}
    private JSONObject safeReport(JSONObject report,JSONObject connection,String at)throws Exception {
        JSONObject row=new JSONObject().put("at",at).put("connectionId",connection.optString("id")).put("connectionName",connection.optString("name")).put("providerId",connection.optString("providerId"));
        for(String field:new String[]{"status","source","usagePeriod","currency","checkedAt"})if(report.opt(field) instanceof String){String value=report.optString(field);row.put(field,value.length()>250?value.substring(0,250):value);}
        for(String field:new String[]{"balanceUsd","usageUsd","tokens","inputTokens","outputTokens","cachedTokens","requests"})if(report.opt(field) instanceof Number){double value=report.optDouble(field);if(Double.isFinite(value))row.put(field,value);}
        return row;
    }
    private JSONArray safeActivityLog(JSONArray rows)throws Exception {JSONArray safe=new JSONArray();if(rows!=null)for(int i=0;i<rows.length()&&safe.length()<ACTIVITY_LIMIT;i++){JSONObject r=rows.optJSONObject(i);if(r==null)continue;String at=timestamp(r.opt("at"));if(at==null)continue;JSONObject c=new JSONObject().put("id",r.optString("connectionId")).put("name",r.optString("connectionName")).put("providerId",r.optString("providerId"));safe.put(safeReport(r,c,at));}return safe;}
    private static URL validateOllama(String value)throws Exception {
        URL u;try{u=new URL(value);}catch(Exception e){throw new SafeException("Use an Ollama URL such as http://192.168.1.10:11434.");}
        if(!("http".equals(u.getProtocol())||"https".equals(u.getProtocol()))||u.getUserInfo()!=null||u.getQuery()!=null||u.getRef()!=null||!(u.getPath().isEmpty()||"/".equals(u.getPath())))throw new SafeException("Use an Ollama server URL without a path or credentials.");
        String host=u.getHost().replace("[","").replace("]","");if(!"localhost".equals(host)){
            if(!host.matches("[0-9a-fA-F:.]+"))throw new SafeException("Ollama must use localhost or a private LAN IP address.");
            InetAddress ip=InetAddress.getByName(host);byte[] b=ip.getAddress();boolean privateIp=ip.isLoopbackAddress()||b.length==4&&((b[0]&255)==10||(b[0]&255)==192&&(b[1]&255)==168||(b[0]&255)==172&&(b[1]&255)>=16&&(b[1]&255)<=31)||b.length==16&&(b[0]&254)==252;
            if(!privateIp)throw new SafeException("Ollama must use localhost or a private LAN IP address.");
        }return u;
    }
    private JSONObject request(JSONObject p)throws Exception {
        JSONObject c=null; synchronized(this){JSONArray cs=config.getJSONArray("connections");for(int i=0;i<cs.length();i++)if(cs.getJSONObject(i).optString("id").equals(p.optString("id")))c=new JSONObject(cs.getJSONObject(i).toString());}
        if(c==null)throw new SafeException("Connection was removed.");String adapter=c.getString("adapter"),path=p.optString("path"),clean=path.split("\\?",2)[0];JSONObject route=routes.getJSONObject(adapter);
        if(path.length()>4000||control(path)||path.contains("#")||path.contains("\\"))throw new SafeException("Invalid API path.");boolean allowed=false;JSONArray paths=route.getJSONArray("paths");for(int i=0;i<paths.length();i++)if(paths.getString(i).equals(clean))allowed=true;if(!allowed)throw new SafeException("API operation is not allowed.");
        String base="ollama".equals(adapter)?validateOllama(c.optString("baseUrl")).toString().replaceAll("/+$",""):route.getString("base");String secret=c.optString("apiKey");if(!"ollama".equals(adapter)&&secret.isEmpty())throw new SafeException("Add an API key first.");
        HttpURLConnection connection=null;
        try{
            connection=(HttpURLConnection)new URL(base+path).openConnection();connection.setConnectTimeout(12000);connection.setReadTimeout(110000);connection.setInstanceFollowRedirects(false);connection.setRequestProperty("Accept","application/json");
            if("anthropic".equals(adapter)){connection.setRequestProperty("x-api-key",secret);connection.setRequestProperty("anthropic-version","2023-06-01");}else if(!secret.isEmpty())connection.setRequestProperty("Authorization","Bearer "+secret);
            if("/api/generate".equals(clean)){
                String model=p.optJSONObject("body")==null?"":p.getJSONObject("body").optString("model");if(model.isEmpty()||model.length()>200||control(model))throw new SafeException("Choose an installed Ollama model.");
                connection.setRequestMethod("POST");connection.setDoOutput(true);connection.setRequestProperty("Content-Type","application/json");byte[] body=new JSONObject().put("model",model).put("prompt","Reply with one short sentence about the night sky.").put("stream",false).put("options",new JSONObject().put("num_predict",32)).toString().getBytes(StandardCharsets.UTF_8);try(OutputStream out=connection.getOutputStream()){out.write(body);}
            }
            int code=connection.getResponseCode();if(code<200||code>=300)throw new SafeException(code==401||code==403?"Key rejected or missing the required scope (HTTP "+code+").":"Provider request failed (HTTP "+code+").");
            String payload=read(connection.getInputStream(),5000000);if(!secret.isEmpty())payload=payload.replace(secret,"[redacted]");try{return new JSONObject(payload);}catch(JSONException e){throw new SafeException("Provider did not return JSON. Its endpoint may be unavailable.");}
        }catch(SocketTimeoutException e){throw new SafeException("Provider timed out. Check the server address and try again.");}catch(IOException e){throw new SafeException("Could not reach the provider. Check your connection or Ollama LAN address.");}finally{if(connection!=null)connection.disconnect();}
    }
    private static byte[] bytes(InputStream source,int maximum)throws Exception {try(InputStream in=source;ByteArrayOutputStream out=new ByteArrayOutputStream()){byte[] buffer=new byte[8192];int count;while((count=in.read(buffer))!=-1){if(out.size()+count>maximum)throw new SafeException("Provider response is too large.");out.write(buffer,0,count);}return out.toByteArray();}}
    private static String read(InputStream in,int maximum)throws Exception{return new String(bytes(in,maximum),StandardCharsets.UTF_8);}
}
