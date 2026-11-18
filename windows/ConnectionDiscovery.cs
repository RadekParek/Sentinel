using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sentinel;
internal sealed record DiscoveredConnection(string ProviderId,string Name,string Adapter,string Source,string? Key=null,string? BaseUrl=null,string? Reason=null);
internal static class ConnectionDiscovery
{
    static readonly Dictionary<string,(string Id,string Name,string Adapter)> Variables=new(StringComparer.OrdinalIgnoreCase){
      ["COARENA_API_KEY"]=("coarena","Coarena","coarena"),["AGENTROUTER_API_KEY"]=("agentrouter","AgentRouter","agentrouter"),["OPENAI_API_KEY"]=("openai","OpenAI API","openai"),["ANTHROPIC_API_KEY"]=("anthropic","Anthropic API","anthropic"),["OPENROUTER_API_KEY"]=("openrouter","OpenRouter","openrouter"),["DEEPSEEK_API_KEY"]=("deepseek","DeepSeek","deepseek"),["GEMINI_API_KEY"]=("gemini","Gemini","manual"),["GOOGLE_API_KEY"]=("gemini","Gemini","manual"),["GROQ_API_KEY"]=("groq","Groq","manual"),["MISTRAL_API_KEY"]=("mistral","Mistral","manual")};
    static bool Valid(string? key)=>!string.IsNullOrWhiteSpace(key)&&key.Length is >=8 and <=4096&&!key.Any(char.IsControl)&&!key.Contains("YOUR_",StringComparison.OrdinalIgnoreCase)&&!key.Contains("REPLACE",StringComparison.OrdinalIgnoreCase);
    static string? Env(string name,EnvironmentVariableTarget scope)=>Environment.GetEnvironmentVariable(name,scope);
    static (string Id,string Name,string Adapter)? Host(string? value)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var u)||u.UserInfo.Length>0)return null;
        return u.Host.ToLowerInvariant() switch {"coarena.ai" => ("coarena","Coarena","coarena"),"agentrouter.org"=>("agentrouter","AgentRouter","agentrouter"),"openrouter.ai"=>("openrouter","OpenRouter","openrouter"),"api.openai.com"=>("openai","OpenAI API","openai"),"api.anthropic.com"=>("anthropic","Anthropic API","anthropic"),"api.deepseek.com"=>("deepseek","DeepSeek","deepseek"),_=>null};
    }
    static string? Read(string path){try{return File.Exists(path)&&new FileInfo(path).Length<2_000_000?File.ReadAllText(path):null;}catch{return null;}}
    public static List<DiscoveredConnection> Scan()
    {
        var result=new List<DiscoveredConnection>();var seen=new HashSet<string>(StringComparer.Ordinal);
        void Add(string id,string name,string adapter,string source,string? key,string? url=null,string? reason=null){if(key!=null&&!Valid(key))return;var signature=id+"|"+(key??source);if(seen.Add(signature))result.Add(new(id,name,adapter,source,key,url,reason));}
        foreach(var scope in new[]{EnvironmentVariableTarget.Process,EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine})foreach(var (variable,info) in Variables)
        {
            var key=Env(variable,scope);if(!Valid(key))continue;
            var baseVar=variable.Replace("_API_KEY","_BASE_URL");var custom=Env(baseVar,scope);
            if(!string.IsNullOrWhiteSpace(custom)&&Host(custom) is not {} matching){Add(info.Id,info.Name,"manual",$"{scope} environment · {variable}",null,null,"Unrecognized API host: add a custom connection. Its key was not imported.");continue;}
            if(!string.IsNullOrWhiteSpace(custom)&&Host(custom) is {} route){Add(route.Id,route.Name,route.Adapter,$"{scope} environment · {variable}",key);continue;}
            Add(info.Id,info.Name,info.Adapter,$"{scope} environment · {variable}",key);
        }
        var home=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach(var file in new[]{Path.Combine(home,".config","opencode","opencode.json"),Path.Combine(home,".config","opencode","opencode.jsonc")})try
        {
            var text=Read(file);if(text==null)continue;var providers=JsonNode.Parse(text,documentOptions:new System.Text.Json.JsonDocumentOptions{CommentHandling=System.Text.Json.JsonCommentHandling.Skip,AllowTrailingCommas=true})?["provider"]?.AsObject();if(providers==null)continue;
            foreach(var (id,value) in providers){if(value is not JsonObject entry)continue;var options=entry["options"]?.AsObject();var url=options?["baseURL"]?.ToString();var route=Host(url);if(string.IsNullOrWhiteSpace(url)){var known=Variables.Values.FirstOrDefault(x=>x.Id==id);if(known.Id!=null)route=known;}if(route is not {} match)continue;var key=options?["apiKey"]?.ToString();var reference=Regex.Match(key??"",@"^\{env:([A-Z][A-Z0-9_]+)\}$");if(reference.Success){foreach(var scope in new[]{EnvironmentVariableTarget.Process,EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine}){var resolved=Env(reference.Groups[1].Value,scope);if(Valid(resolved))Add(match.Id,match.Name,match.Adapter,"OpenCode provider configuration",resolved);}}else if(Valid(key))Add(match.Id,match.Name,match.Adapter,"OpenCode provider configuration",key);else Add(match.Id,match.Name,match.Adapter,"OpenCode provider configuration",null,null,"Provider configured; no reusable API key found in its configuration");}
        }catch{}
        foreach(var file in new[]{Path.Combine(home,".claude","settings.json"),Path.Combine(home,".claude","settings.local.json")})try
        {
            var text=Read(file);if(text==null)continue;var env=JsonNode.Parse(text)?["env"]?.AsObject();if(env==null)continue;
            foreach(var (variable,info) in Variables){var key=env[variable]?.ToString();if(!Valid(key))continue;var custom=env[variable.Replace("_API_KEY","_BASE_URL")]?.ToString();if(string.IsNullOrWhiteSpace(custom))Add(info.Id,info.Name,info.Adapter,"Claude settings · "+variable,key);else if(Host(custom) is {} route)Add(route.Id,route.Name,route.Adapter,"Claude provider settings · "+variable,key);else Add(info.Id,info.Name,"manual","Claude custom provider settings",null,null,"Unrecognized provider host: add a custom connection. Its key was not imported.");}
            var token=env["ANTHROPIC_AUTH_TOKEN"]?.ToString();var baseUrl=env["ANTHROPIC_BASE_URL"]?.ToString();if(Valid(token)){var route=Host(baseUrl);if(route is {} match)Add(match.Id,match.Name,match.Adapter,"Claude provider settings",token);else if(string.IsNullOrWhiteSpace(baseUrl))Add("anthropic","Anthropic API","anthropic","Claude provider settings",token);}
        }catch{}
        var codex=Read(Path.Combine(home,".codex","config.toml"));
        if(codex!=null)
        {
            var blocks=Regex.Split(codex,@"(?m)^\[");foreach(var block in blocks){var baseMatch=Regex.Match(block,@"(?m)^\s*base_url\s*=\s*""([^""]+)""");var envMatch=Regex.Match(block,@"(?m)^\s*env_key\s*=\s*""([A-Z][A-Z0-9_]+)""");if(!baseMatch.Success||!envMatch.Success)continue;var route=Host(baseMatch.Groups[1].Value);if(route is not {} match)continue;foreach(var scope in new[]{EnvironmentVariableTarget.Process,EnvironmentVariableTarget.User,EnvironmentVariableTarget.Machine}){var key=Env(envMatch.Groups[1].Value,scope);if(Valid(key))Add(match.Id,match.Name,match.Adapter,"Codex provider configuration",key);}}
            Add("codex","Codex","manual","Local Codex configuration",null,null,"Local usage is collected from session records; subscription billing stays in the official dashboard");
        }
        if(Directory.Exists(Path.Combine(home,".claude","projects")))Add("claude","Claude Code","manual","Local Claude Code session records",null,null,"Local activity available; subscription billing stays in the official dashboard");
        return result;
    }
}
