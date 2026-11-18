using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Sentinel;
internal sealed class SafeException(string message):Exception(message);
internal sealed class NativeBackend
{
    readonly object gate=new(); readonly string vaultPath; JsonObject config; readonly JsonObject routes;
    readonly LocalScanner scanner=new();
    readonly HttpClient http=new(new HttpClientHandler {AllowAutoRedirect=false,UseProxy=false}) {Timeout=TimeSpan.FromSeconds(110)};
    JsonObject local=new(); string? vaultError;
    readonly OllamaMeter meter;
    JsonArray discoveries=new(); CancellationTokenSource? signIn;
    public NativeBackend(string? storageRoot=null)
    {
        var root=storageRoot??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Sentinel");Directory.CreateDirectory(root);vaultPath=Path.Combine(root,"vault.bin");
        meter=new OllamaMeter(root);
        using var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("web.routes.json")!;routes=JsonNode.Parse(s)!.AsObject();
        config=new JsonObject{{"connections",new JsonArray(new JsonObject{{"id","ollama-local"},{"providerId","ollama"},{"name","Ollama · this computer"},{"adapter","ollama"},{"baseUrl","http://127.0.0.1:11434"}})},{"snapshots",new JsonObject()},{"settings",new JsonObject{{"refreshSeconds",300}}}};
        if(File.Exists(vaultPath))try { var clear=ProtectedData.Unprotect(File.ReadAllBytes(vaultPath),null,DataProtectionScope.CurrentUser);config=JsonNode.Parse(clear)!.AsObject();CryptographicOperations.ZeroMemory(clear); }
        catch {vaultError="Your encrypted vault could not be opened. Existing data was preserved. Restore access to your Windows account before saving connections.";}
        config["activityLog"]??=new JsonArray();
    }
    static string Str(JsonObject p,string key)=>p[key]?.GetValue<string>()??"";
    static JsonObject Copy(JsonObject value)=>(JsonObject)value.DeepClone();
    JsonArray Connections=>config["connections"]!.AsArray();
    JsonObject State()
    {
        var output=new JsonArray();
        foreach(var item in Connections) {var c=Copy(item!.AsObject());var key=Str(c,"apiKey");c.Remove("apiKey");c["hasKey"]=key.Length>0;c["keyHint"]=key.Length>=4?"••••"+key[^4..]:key.Length>0?"••••":"No key";output.Add(c);}
        return new JsonObject{{"platform","windows"},{"version","1.1.0"},{"connections",output},{"activityLog",config["activityLog"]!.DeepClone()},{"discoveries",discoveries.DeepClone()},{"snapshots",config["snapshots"]?.DeepClone()??new JsonObject()},{"settings",config["settings"]!.DeepClone()},{"localUsage",local.DeepClone()},{"refreshedAt",config["refreshedAt"]?.DeepClone()},{"vaultError",vaultError}};
    }
    void Save()
    {
        if(vaultError!=null)throw new SafeException(vaultError);
        var clear=Encoding.UTF8.GetBytes(config.ToJsonString());var encrypted=ProtectedData.Protect(clear,null,DataProtectionScope.CurrentUser);CryptographicOperations.ZeroMemory(clear);
        var temp=vaultPath+".tmp";File.WriteAllBytes(temp,encrypted);File.Move(temp,vaultPath,true);
    }
    public async Task<JsonNode> Handle(string method,JsonObject p)
    {
        if(method=="cancelSignIn"){signIn?.Cancel();return new JsonObject{{"ok",true}};}
        if(method=="signInOpenRouter")
        {
            lock(gate){if(vaultError!=null)throw new SafeException(vaultError);if(signIn!=null)throw new SafeException("A sign-in is already open. Finish or cancel it first.");signIn=new CancellationTokenSource();}
            try{var key=await OpenRouterSignIn.Authorize(signIn.Token);return await Handle("saveConnection",new JsonObject{{"providerId","openrouter"},{"name","OpenRouter · authorized account"},{"adapter","openrouter"},{"apiKey",key}});}finally{lock(gate){signIn?.Dispose();signIn=null;}}
        }
        if(method is "scanConnections" or "importLocalConnections")
        {
            var found=await Task.Run(ConnectionDiscovery.Scan);
            lock(gate)
            {
                if(method=="importLocalConnections"&&vaultError!=null)throw new SafeException(vaultError);
                discoveries=new();int imported=0,existing=0;
                foreach(var f in found)
                {
                    var c=f.Key==null?Connections.FirstOrDefault(x=>x?["providerId"]?.ToString()==f.ProviderId)?.AsObject():Connections.FirstOrDefault(x=>x?["providerId"]?.ToString()==f.ProviderId&&x?["apiKey"]?.ToString()==f.Key)?.AsObject();
                    if(c!=null)existing++;
                    if(method=="importLocalConnections"&&c!=null&&Str(c,"importSource").Length>0&&f.Adapter=="agentrouter"&&Str(c,"adapter")=="manual"){c["adapter"]=f.Adapter;config["snapshots"]!.AsObject().Remove(Str(c,"id"));}
                    if(c==null&&f.Key!=null&&method=="importLocalConnections"&&Connections.Count<100){c=new JsonObject{{"id",Guid.NewGuid().ToString()},{"providerId",f.ProviderId},{"name",f.Name+" · this PC"},{"adapter",f.Adapter},{"apiKey",f.Key},{"importSource",f.Source}};Connections.Add(c);imported++;}
                    var localActivity=f.ProviderId is "codex" or "claude";discoveries.Add(new JsonObject{{"providerId",f.ProviderId},{"name",f.Name},{"adapter",f.Adapter},{"source",f.Source},{"status",f.Key!=null?"credential found":localActivity?"local activity":"configured"},{"connected",c!=null||localActivity},{"connectionId",c?["id"]?.DeepClone()},{"reason",f.Reason}});
                }
                if(method=="scanConnections")return new JsonObject{{"available",true},{"discoveries",discoveries.DeepClone()},{"checkedAt",DateTime.UtcNow.ToString("O")}};
                Save();var state=State();state["importResult"]=new JsonObject{{"connected",imported},{"existing",existing},{"detail",$"{imported} new provider connections added. {existing} were already configured. Local Codex and Claude records are collected separately."}};return state;
            }
        }
        if(method=="saveSettings")
        {
            var seconds=p["refreshSeconds"]?.GetValue<int>()??300;if(seconds!=0&&seconds!=60&&seconds!=300&&seconds!=900)throw new SafeException("Invalid refresh interval.");
            if(vaultError!=null)throw new SafeException(vaultError);
            if(p["ollamaMeter"]!=null)await meter.SetEnabled(p["ollamaMeter"]!.GetValue<bool>());
            lock(gate){config["settings"]!["refreshSeconds"]=seconds;config["settings"]!["ollamaMeter"]=meter.Enabled;if(p.ContainsKey("theme")){var theme=Str(p,"theme");if(theme is not "light" and not "dark")throw new SafeException("Invalid appearance.");config["settings"]!["theme"]=theme;}Save();return State();}
        }
        if(method=="getOllamaUsage")
        {
            JsonObject? c;lock(gate){c=Connections.FirstOrDefault(x=>x?["id"]?.GetValue<string>()==Str(p,"id"))?.AsObject();}
            if(c==null||Str(c,"adapter")!="ollama")return new JsonObject{{"available",false}};
            var url=ValidateOllama(Str(c,"baseUrl"));return url.IsLoopback&&url.Port==11434?meter.Usage():new JsonObject{{"available",false}};
        }
        if(method=="getState"&&config["settings"]?["ollamaMeter"]?.ToString()=="true"&&!meter.Enabled)
        {try{await meter.SetEnabled(true);}catch{lock(gate){config["settings"]!["ollamaMeter"]=false;}}}
        if(method=="request")return await Request(p);
        if(method=="scanLocal") { var result=await Task.Run(()=>scanner.Scan());lock(gate){local=result;}return result.DeepClone(); }
        lock(gate)
        {
            switch(method)
            {
                case "getState":return State();
                case "saveConnection":
                    if(vaultError!=null)throw new SafeException(vaultError);
                    var adapter=Str(p,"adapter");if(!routes.ContainsKey(adapter))throw new SafeException("Connector is not supported.");
                    var name=Str(p,"name").Trim();var provider=Str(p,"providerId");var key=Str(p,"apiKey");
                    if(name.Length is <1 or >120||provider.Length is <1 or >120||name.Any(char.IsControl)||provider.Any(char.IsControl)||key.Length>4096||key.Any(char.IsControl))throw new SafeException("Check the connection name and key.");
                    foreach(var field in new[]{"accountLabel","walletGroup"})if(p.ContainsKey(field)){var label=Str(p,field).Trim();if(label.Length>120||label.Any(char.IsControl))throw new SafeException("Invalid account label or wallet group.");}
                    var baseUrl=Str(p,"baseUrl");if(adapter=="ollama")ValidateOllama(baseUrl);
                    var external=Str(p,"url");if(external.Length>0&&(!Uri.TryCreate(external,UriKind.Absolute,out var link)||link.Scheme!="https"||link.UserInfo.Length>0))throw new SafeException("Use an HTTPS dashboard URL.");
                    if(p.ContainsKey("manualBalance")&&p["manualBalance"]!=null&&(!double.TryParse(p["manualBalance"]!.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var amount)||!double.IsFinite(amount)||Math.Abs(amount)>1e12))throw new SafeException("Invalid wallet amount.");
                    var id=Str(p,"id");var current=Connections.FirstOrDefault(x=>x?["id"]?.GetValue<string>()==id)?.AsObject();
                    if(current!=null&&(Str(current,"adapter")!=adapter||Str(current,"providerId")!=provider))throw new SafeException("Create a new connection to change its provider.");
                    if(current==null) {if(Connections.Count>=100)throw new SafeException("Connection limit reached.");current=new JsonObject{{"id",Guid.NewGuid().ToString()}};Connections.Add(current);}
                    current["providerId"]=provider;current["name"]=name;current["adapter"]=adapter;
                    foreach(var field in new[]{"accountLabel","walletGroup"})if(p.ContainsKey(field)){var label=Str(p,field).Trim();if(label.Length>120||label.Any(char.IsControl))throw new SafeException("Invalid account label or wallet group.");current[field]=label;}
                    if(external.Length>0)current["url"]=external;
                    if(p["clearKey"]?.ToString()=="true")current.Remove("apiKey");else if(key.Length>0)current["apiKey"]=key;
                    if(adapter=="ollama") {var url=Str(p,"baseUrl");ValidateOllama(url);current["baseUrl"]=url.TrimEnd('/');}
                    if(p.ContainsKey("manualBalance")) {if(p["manualBalance"]!=null&&(!double.TryParse(p["manualBalance"]!.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var balance)||!double.IsFinite(balance)||Math.Abs(balance)>1e12))throw new SafeException("Invalid wallet amount.");if(current["manualBalance"]?.ToString()!=p["manualBalance"]?.ToString())current["manualUpdatedAt"]=DateTime.UtcNow.ToString("O");current["manualBalance"]=p["manualBalance"]?.DeepClone();}
                    // A replaced key invalidates all previously cached cloud amounts.
                    config["snapshots"]!.AsObject().Remove(Str(current,"id"));Save();return State();
                case "removeConnection":
                    if(vaultError!=null)throw new SafeException(vaultError);
                    var removeId=Str(p,"id");var remove=Connections.FirstOrDefault(x=>x?["id"]?.GetValue<string>()==removeId);if(remove!=null)Connections.Remove(remove);config["snapshots"]!.AsObject().Remove(removeId);Save();return State();
                case "clearKey":
                    if(vaultError!=null)throw new SafeException(vaultError);var clear=Connections.FirstOrDefault(x=>x?["id"]?.ToString()==Str(p,"id"))?.AsObject()??throw new SafeException("Connection was removed.");clear.Remove("apiKey");config["snapshots"]!.AsObject().Remove(Str(clear,"id"));Save();return State();
                case "clearActivityLog":
                    if(vaultError!=null)throw new SafeException(vaultError);config["activityLog"]=new JsonArray();Save();return State();
                case "saveSettings":
                    if(vaultError!=null)throw new SafeException(vaultError);
                    var seconds=p["refreshSeconds"]?.GetValue<int>()??300;if(seconds!=0&&seconds!=60&&seconds!=300&&seconds!=900)throw new SafeException("Invalid refresh interval.");config["settings"]!["refreshSeconds"]=seconds;Save();return State();
                case "saveSnapshots":
                    if(vaultError!=null)throw new SafeException(vaultError);
                    var snapshots=p["snapshots"]?.AsObject()??new();if(snapshots.ToJsonString().Length>5_000_000)throw new SafeException("Snapshot is too large.");
                    var filtered=new JsonObject();foreach(var c in Connections) {var cid=c!["id"]!.GetValue<string>();if(snapshots[cid]!=null)filtered[cid]=snapshots[cid]!.DeepClone();}
                    var log=config["activityLog"]!.AsArray();var at=DateTime.UtcNow.ToString("O");foreach(var c in Connections){var cid=c!["id"]!.ToString();if(filtered[cid] is not JsonObject snap)continue;var row=new JsonObject{{"at",at},{"connectionId",cid},{"connectionName",Str(c.AsObject(),"name")},{"providerId",Str(c.AsObject(),"providerId")}};foreach(var field in new[]{"status","source","usagePeriod","currency","checkedAt"})if(snap[field] is JsonValue val&&val.TryGetValue<string>(out var text))row[field]=text.Length<=250?text:text[..250];foreach(var field in new[]{"balanceUsd","usageUsd","tokens","inputTokens","outputTokens","cachedTokens","requests"})if(snap[field] is JsonValue val&&val.TryGetValue<double>(out var loggedAmount)&&double.IsFinite(loggedAmount))row[field]=loggedAmount;log.Insert(0,row);}while(log.Count>500)log.RemoveAt(log.Count-1);
                    config["snapshots"]=filtered;config["refreshedAt"]=p["refreshedAt"]?.DeepClone();Save();return State();
                case "openExternal":OpenLink(Str(p,"url"));return new JsonObject{{"ok",true}};
                case "copyKey":
                    var secret=Str(Connections.FirstOrDefault(x=>x?["id"]?.GetValue<string>()==Str(p,"id"))?.AsObject()??throw new SafeException("Connection was removed."),"apiKey");
                    if(secret.Length==0)throw new SafeException("No key is stored for this connection.");
                    Clipboard.SetText(secret);
                    var timer=new System.Windows.Forms.Timer{Interval=60000};timer.Tick+=(_,_)=>{try{if(Clipboard.ContainsText()&&Clipboard.GetText()==secret)Clipboard.Clear();}catch{}timer.Stop();timer.Dispose();};timer.Start();return new JsonObject{{"ok",true}};
                default:throw new SafeException("Unsupported operation.");
            }
        }
    }
    public static void OpenLink(string url)
    {
        if(Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme=="https"&&u.UserInfo.Length==0)try {Process.Start(new ProcessStartInfo(u.AbsoluteUri){UseShellExecute=true});}catch{}
    }
    static bool Private(IPAddress address)
    {
        if(IPAddress.IsLoopback(address))return true;
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();var b=address.GetAddressBytes();
        return b.Length==4&&(b[0]==10||b[0]==192&&b[1]==168||b[0]==172&&b[1]>=16&&b[1]<=31)||b.Length==16&&(b[0]&0xfe)==0xfc;
    }
    internal static Uri ValidateOllama(string value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var u)||!(u.Scheme=="http"||u.Scheme=="https")||u.UserInfo.Length>0||u.AbsolutePath!="/"||u.Query.Length>0||u.Fragment.Length>0)throw new SafeException("Use an Ollama server URL such as http://192.168.1.10:11434.");
        // IP literals avoid DNS rebinding and accidentally sending private keys to a public host.
        if(u.Host!="localhost"&&(!IPAddress.TryParse(u.Host.Trim('[',']'),out var ip)||!Private(ip)))throw new SafeException("Ollama must use localhost or a private LAN IP address.");
        return u;
    }
    async Task<JsonNode> Request(JsonObject p)
    {
        JsonObject c;lock(gate){c=Copy(Connections.FirstOrDefault(x=>x?["id"]?.GetValue<string>()==Str(p,"id"))?.AsObject()??throw new SafeException("Connection was removed."));}
        var adapter=Str(c,"adapter");var route=routes[adapter]?.AsObject()??throw new SafeException("Unknown connector.");var path=Str(p,"path");
        if(path.Length>4000||!path.StartsWith('/')||path.Any(char.IsControl)||path.Contains('#')||path.Contains('\\'))throw new SafeException("Invalid API path.");
        var clean=path.Split('?')[0];if(!route["paths"]!.AsArray().Any(x=>x?.GetValue<string>()==clean))throw new SafeException("API operation is not allowed.");
        var baseUrl=adapter=="ollama"?ValidateOllama(Str(c,"baseUrl")).GetLeftPart(UriPartial.Authority):Str(route,"base");var uri=new Uri(baseUrl+path);
        var key=Str(c,"apiKey");if(adapter!="ollama"&&key.Length==0)throw new SafeException("Add an API key first.");
        using var req=new HttpRequestMessage(clean=="/api/generate"?HttpMethod.Post:HttpMethod.Get,uri);
        if(clean=="/api/generate")
        {
            var model=p["body"]?["model"]?.GetValue<string>()??"";if(model.Length is <1 or >200||model.Any(char.IsControl))throw new SafeException("Choose an installed Ollama model.");
            req.Content=new StringContent(new JsonObject{{"model",model},{"prompt","Reply with one short sentence about the night sky."},{"stream",false},{"options",new JsonObject{{"num_predict",32}}}}.ToJsonString(),Encoding.UTF8,"application/json");
        }
        if(adapter=="anthropic") {req.Headers.Add("x-api-key",key);req.Headers.Add("anthropic-version","2023-06-01");}
        else if(key.Length>0)req.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
        try
        {
            using var response=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead);
            if(!response.IsSuccessStatusCode)throw new SafeException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden?$"Key rejected or missing the required scope (HTTP {(int)response.StatusCode}).":$"Provider request failed (HTTP {(int)response.StatusCode}).");
            if(response.Content.Headers.ContentLength>5_000_000)throw new SafeException("Provider response is too large.");
            await using var stream=await response.Content.ReadAsStreamAsync();using var ms=new MemoryStream();var buf=new byte[8192];int count;while((count=await stream.ReadAsync(buf))>0){if(ms.Length+count>5_000_000)throw new SafeException("Provider response is too large.");ms.Write(buf,0,count);}
            var text=Encoding.UTF8.GetString(ms.ToArray());if(key.Length>0)text=text.Replace(key,"[redacted]",StringComparison.Ordinal);
            try{var json=JsonNode.Parse(text)??throw new SafeException("Provider returned an empty response.");if(adapter=="ollama"&&clean=="/api/generate"&&uri.IsLoopback&&uri.Port==11434&&json is JsonObject measured)meter.Record(measured,"Sentinel benchmark");return json;}catch(System.Text.Json.JsonException){throw new SafeException("Provider did not return JSON. Its endpoint may be unavailable.");}
        }
        catch(TaskCanceledException){throw new SafeException("Provider timed out. Check the server address and try again.");}
        catch(HttpRequestException){throw new SafeException("Could not reach the provider. Check your connection or Ollama server address.");}
    }
}
