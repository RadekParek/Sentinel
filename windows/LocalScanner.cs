using System.Text;
using System.Text.Json.Nodes;

namespace Sentinel;
internal sealed class LocalScanner
{
    internal sealed record Usage(string Key,DateTimeOffset At,long Input,long Output,long Cached,string Session);
    sealed class FileCache
    {
        public long Offset,Length;public DateTime Modified;public long[]? Previous;
        public List<Usage> Events=new();public JsonArray Limits=new();public DateTimeOffset LimitAt;
    }
    readonly Dictionary<string,FileCache> cache=new(StringComparer.OrdinalIgnoreCase); readonly object sync=new();
    static long N(JsonNode? v) => long.TryParse(v?.ToString(),out var result)?Math.Max(0,result):0;
    static long[] Counts(JsonNode? value)=>[N(value?["input_tokens"]),N(value?["output_tokens"]),N(value?["cached_input_tokens"])];
    static bool Equal(long[] a,long[] b)=>a.SequenceEqual(b);
    public JsonObject Scan()
    {
        lock(sync)
        {
            var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);var cutoff=DateTimeOffset.UtcNow.Date.AddDays(-29);
            return new JsonObject{{"codex",Collect("codex",[Path.Combine(home,".codex","sessions"),Path.Combine(home,".codex","archived_sessions")],cutoff)},{"claude",Collect("claude",[Path.Combine(home,".claude","projects")],cutoff)}};
        }
    }
    JsonObject Collect(string kind,string[] roots,DateTimeOffset cutoff)
    {
        var all=new Dictionary<string,Usage>();var seenFiles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var errors=0;JsonArray limits=new();DateTimeOffset limitAt=default;
        foreach(var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;try{files=Directory.EnumerateFiles(root,"*.jsonl",SearchOption.AllDirectories).ToArray();}catch{errors++;continue;}
            foreach(var path in files)
            {
                try
                {
                    var info=new FileInfo(path);if(info.LastWriteTimeUtc<cutoff.UtcDateTime)continue;seenFiles.Add(path);
                    if(!cache.TryGetValue(path,out var state)||info.Length<state.Offset||info.Length==state.Length&&info.LastWriteTimeUtc!=state.Modified){state=new FileCache();cache[path]=state;}
                    if(info.Length!=state.Length||info.LastWriteTimeUtc!=state.Modified)
                    {
                        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,65536,FileOptions.SequentialScan);stream.Position=state.Offset;
                        foreach(var (line,end) in CompleteLines(stream)) {Parse(kind,line,state,Path.GetFileNameWithoutExtension(path));state.Offset=end;}
                        state.Length=info.Length;state.Modified=info.LastWriteTimeUtc;
                    }
                    foreach(var e in state.Events.Where(x=>x.At>=cutoff))
                    {
                        if(all.TryGetValue(e.Key,out var prior))all[e.Key]=prior with {At=prior.At>e.At?prior.At:e.At,Input=Math.Max(prior.Input,e.Input),Output=Math.Max(prior.Output,e.Output),Cached=Math.Max(prior.Cached,e.Cached)};
                        else all[e.Key]=e;
                    }
                    if(state.LimitAt>limitAt){limitAt=state.LimitAt;limits=(JsonArray)state.Limits.DeepClone();}
                }
                catch(IOException){errors++;}catch(UnauthorizedAccessException){errors++;}
            }
        }
        // Cache only recent files; no prompt text is retained.
        foreach(var key in cache.Keys.Where(x=>roots.Any(r=>x.StartsWith(r,StringComparison.OrdinalIgnoreCase))&&!seenFiles.Contains(x)).ToArray())cache.Remove(key);
        var exists=roots.Any(Directory.Exists);var status=!exists?"unavailable":errors>0?"error":"ok";
        var result=new JsonObject{{"status",status},{"checkedAt",DateTimeOffset.UtcNow.ToString("o")},{"source",kind=="codex"?"Local Codex session records · last 30 days":"Local Claude Code session records · last 30 days"},{"detail",!exists?"No local session folder found on this Windows account.":$"Observed tokens from this computer; these are not subscription charges. Duplicate records are merged.{(errors>0?$" {errors} files could not be read; the displayed counts are partial.":"")}"}};
        if(exists)
        {
            result["inputTokens"]=all.Values.Sum(x=>x.Input);result["outputTokens"]=all.Values.Sum(x=>x.Output);result["cachedTokens"]=all.Values.Sum(x=>x.Cached);result["tokens"]=all.Values.Sum(x=>x.Input+x.Output);result["requests"]=all.Count;result["sessions"]=all.Values.Select(x=>x.Session).Distinct().Count();
            result["history"]=new JsonArray(all.Values.GroupBy(x=>x.At.UtcDateTime.ToString("yyyy-MM-dd")).OrderBy(x=>x.Key).Select(x=>(JsonNode)new JsonObject{{"date",x.Key},{"tokens",x.Sum(e=>e.Input+e.Output)}}).ToArray());
            result["records"]=new JsonArray(all.Values.OrderByDescending(x=>x.At).Take(500).Select(x=>(JsonNode)new JsonObject{{"at",x.At.ToString("o")},{"tokens",x.Input+x.Output},{"inputTokens",x.Input},{"outputTokens",x.Output},{"cachedTokens",x.Cached}}).ToArray());
            if(limits.Count>0){result["rateLimits"]=limits;result["rateLimitsCheckedAt"]=limitAt.ToString("o");}
        }
        return result;
    }
    // Track byte offsets only after a newline. An actively-written partial record is retried next refresh.
    static IEnumerable<(string Line,long End)> CompleteLines(FileStream stream)
    {
        var buffer=new byte[65536];using var line=new MemoryStream();long position=stream.Position;int count;
        while((count=stream.Read(buffer))>0)
        {
            for(var i=0;i<count;i++) {position++;if(buffer[i]==10){yield return (Encoding.UTF8.GetString(line.GetBuffer(),0,(int)line.Length),position);line.SetLength(0);}else{if(line.Length>20_000_000)throw new IOException("Session record too large.");line.WriteByte(buffer[i]);}}
        }
    }
    static void Parse(string kind,string line,FileCache state,string session)
    {
        if(kind=="codex"&&!line.Contains("\"token_count\"",StringComparison.Ordinal)||kind=="claude"&&(!line.Contains("\"usage\"",StringComparison.Ordinal)||!line.Contains("\"assistant\"",StringComparison.Ordinal)))return;
        JsonNode? root;try{root=JsonNode.Parse(line);}catch{return;}
        if(root is not JsonObject)return;
        if(!DateTimeOffset.TryParse(root?["timestamp"]?.ToString(),out var at))return;
        if(kind=="codex")
        {
            var payload=root?["payload"];if(payload is not JsonObject||payload?["type"]?.ToString()!="token_count")return;
            if(payload["rate_limits"] is JsonObject rate && at>=state.LimitAt)
            {
                var entries=new JsonArray();foreach(var name in new[]{"primary","secondary"})if(rate[name]?["used_percent"]!=null)
                {var percent=rate[name]!["used_percent"]!.DeepClone();var width=N(rate[name]?["window_minutes"]);var r=new JsonObject{{"name",width>0?$"{width/60d:0.#}h limit":name},{"usedPercent",percent}};if(N(rate[name]?["resets_at"])>0)r["resetsAt"]=DateTimeOffset.FromUnixTimeSeconds(N(rate[name]?["resets_at"])).ToString("o");entries.Add(r);}
                state.Limits=entries;state.LimitAt=at;
            }
            var info=payload["info"];if(info is not JsonObject||info["total_token_usage"] is not JsonObject)return;
            var total=Counts(info["total_token_usage"]);var last=info["last_token_usage"]==null?null:Counts(info["last_token_usage"]);long[] delta;
            if(state.Previous!=null&&Equal(total,state.Previous))return;
            if(state.Previous==null)delta=last??total;
            else if(total[0]>=state.Previous[0]&&total[1]>=state.Previous[1]&&total[2]>=state.Previous[2])delta=total.Zip(state.Previous,(a,b)=>a-b).ToArray();
            else delta=last??total;
            state.Previous=total;if(delta[0]+delta[1]==0)return;
            var signature=$"codex:{at:o}:{total[0]}:{total[1]}:{total[2]}";
            state.Events.Add(new Usage(signature,at,delta[0],delta[1],delta[2],session));
        }
        else
        {
            if(root?["type"]?.ToString()!="assistant"||root["message"] is not JsonObject)return;var u=root["message"]?["usage"];if(u is not JsonObject)return;
            var id=root["message"]?["id"]?.ToString()??root["requestId"]?.ToString();if(string.IsNullOrWhiteSpace(id))return;
            state.Events.Add(new Usage("claude:"+id,at,N(u["input_tokens"])+N(u["cache_creation_input_tokens"])+N(u["cache_read_input_tokens"]),N(u["output_tokens"]),N(u["cache_read_input_tokens"]),session));
        }
    }
    internal static JsonObject SelfTest()
    {
        var state=new FileCache();
        string C(long input,long output,string time)=>new JsonObject{{"timestamp",time},{"payload",new JsonObject{{"type","token_count"},{"info",new JsonObject{{"total_token_usage",new JsonObject{{"input_tokens",input},{"output_tokens",output},{"cached_input_tokens",0}}},{"last_token_usage",new JsonObject{{"input_tokens",100},{"output_tokens",20},{"cached_input_tokens",0}}}}}}}}.ToJsonString();
        Parse("codex",C(100,20,"2026-11-18T10:00:00Z"),state,"s1");Parse("codex",C(100,20,"2026-11-18T10:00:01Z"),state,"s1");Parse("codex",C(220,50,"2026-11-18T10:00:02Z"),state,"s1");
        var checks=new JsonObject{{"codexCumulativeNotDoubleCounted",state.Events.Count==2&&state.Events.Sum(x=>x.Input+x.Output)==270}};
        var inherited=new FileCache();Parse("codex",C(10000,2000,"2026-11-18T10:00:00Z"),inherited,"s2");checks["inheritedBaselineNotCounted"]=inherited.Events.Sum(x=>x.Input+x.Output)==120;
        var claude=new FileCache();string L(long output)=>new JsonObject{{"timestamp","2026-11-18T10:00:00Z"},{"type","assistant"},{"message",new JsonObject{{"id","msg_test"},{"usage",new JsonObject{{"input_tokens",100},{"cache_creation_input_tokens",50},{"cache_read_input_tokens",25},{"output_tokens",output}}}}}}.ToJsonString();
        Parse("claude",L(5),claude,"s");Parse("claude",L(10),claude,"s");checks["claudeChunksMergeable"]=claude.Events.Select(x=>x.Key).Distinct().Count()==1&&claude.Events.Max(x=>x.Input)==175&&claude.Events.Max(x=>x.Output)==10;
        bool Reject(string url){try{NativeBackend.ValidateOllama(url);return false;}catch(SafeException){return true;}}
        checks["publicOllamaRejected"]=Reject("http://example.com:11434")&&Reject("http://8.8.8.8:11434")&&Reject("http://127.0.0.1:11434/evil")&&Reject("http://user:pass@127.0.0.1:11434");
        checks["privateOllamaAccepted"]=NativeBackend.ValidateOllama("http://192.168.1.4:11434").Host=="192.168.1.4";
        var clear=Encoding.UTF8.GetBytes("fixture-secret-not-a-real-key");var cipher=System.Security.Cryptography.ProtectedData.Protect(clear,null,System.Security.Cryptography.DataProtectionScope.CurrentUser);checks["vaultEncryptsAndRoundTrips"]=!Encoding.UTF8.GetString(cipher).Contains("fixture-secret")&&System.Security.Cryptography.ProtectedData.Unprotect(cipher,null,System.Security.Cryptography.DataProtectionScope.CurrentUser).SequenceEqual(clear);
        return new JsonObject{{"passed",checks.All(x=>x.Value!.GetValue<bool>())},{"checks",checks}};
    }
}
