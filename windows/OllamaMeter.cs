using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json.Nodes;

namespace Sentinel;
/// <summary>A local opt-in forwarding server. Only final token metadata is persisted.</summary>
internal sealed class OllamaMeter
{
    readonly string ledger;readonly object gate=new();WebApplication? app;readonly string upstream;readonly SemaphoreSlim startGate=new(1,1);
    readonly HttpClient http=new(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=Timeout.InfiniteTimeSpan};
    public bool Enabled=>app!=null;
    public OllamaMeter(string folder,string target="http://127.0.0.1:11434"){ledger=Path.Combine(folder,"ollama-usage.jsonl");upstream=target;}
    public async Task SetEnabled(bool enabled)
    {
        await startGate.WaitAsync();try{
        if(enabled==Enabled)return;
        if(!enabled){if(app!=null){await app.StopAsync();await app.DisposeAsync();app=null;}return;}
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:11435");builder.WebHost.ConfigureKestrel(o=>o.Limits.MaxRequestBodySize=16*1024*1024);
        var server=builder.Build();server.Run(Forward);
        try{await server.StartAsync();app=server;}catch{await server.DisposeAsync();throw new SafeException("The Ollama usage meter could not start. Port 11435 may already be in use.");}
        }finally{startGate.Release();}
    }
    async Task Forward(HttpContext ctx)
    {
        var path=ctx.Request.Path.Value??"";
        var read=new[]{"/api/tags","/api/ps","/api/version","/v1/models"};var write=new[]{"/api/generate","/api/chat","/api/show","/api/embed","/api/embeddings","/v1/chat/completions","/v1/completions"};
        if(!(ctx.Request.Method=="GET"&&read.Contains(path)||ctx.Request.Method=="POST"&&write.Contains(path))){ctx.Response.StatusCode=404;return;}
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);cancel.CancelAfter(TimeSpan.FromMinutes(10));
        JsonObject? measurement=null;
        try
        {
            using var req=new HttpRequestMessage(new HttpMethod(ctx.Request.Method),upstream+path+ctx.Request.QueryString);
            if(ctx.Request.Method=="POST"){using var body=new MemoryStream();await ctx.Request.Body.CopyToAsync(body,cancel.Token);if(body.Length>16*1024*1024){ctx.Response.StatusCode=413;return;}req.Content=new ByteArrayContent(body.ToArray());req.Content.Headers.TryAddWithoutValidation("Content-Type",ctx.Request.ContentType??"application/json");}
            if(ctx.Request.Headers.TryGetValue("Authorization",out var auth))req.Headers.TryAddWithoutValidation("Authorization",auth.ToString());
            using var response=await http.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,cancel.Token);ctx.Response.StatusCode=(int)response.StatusCode;ctx.Response.ContentType=response.Content.Headers.ContentType?.ToString()??"application/json";
            await using var stream=await response.Content.ReadAsStreamAsync(cancel.Token);using var reader=new StreamReader(stream,Encoding.UTF8);
            while(await reader.ReadLineAsync(cancel.Token) is { } line)
            {
                if(response.IsSuccessStatusCode&&write.Contains(path)&&line.Length<2_000_000&&(line.Contains("\"eval_count\"")||line.Contains("\"usage\"")))
                {var candidate=line.StartsWith("data:",StringComparison.Ordinal)?line[5..].Trim():line;try{var parsed=JsonNode.Parse(candidate)?.AsObject();if(parsed!=null&&Extract(parsed)!=null)measurement=parsed;}catch{}}
                await ctx.Response.WriteAsync(line+"\n",cancel.Token);await ctx.Response.Body.FlushAsync(cancel.Token);
            }
            if(measurement!=null)Record(measurement,"Ollama proxy");
        }
        catch{if(!ctx.Response.HasStarted){ctx.Response.StatusCode=502;await ctx.Response.WriteAsync("{\"error\":\"Ollama could not be reached at 127.0.0.1:11434\"}");}}
    }
    static long? Number(JsonNode? n)=>long.TryParse(n?.ToString(),out var value)&&value>=0?value:null;
    static (long Input,long Output)? Extract(JsonObject r)
    {
        var input=Number(r["prompt_eval_count"]??r["usage"]?["prompt_tokens"]);var output=Number(r["eval_count"]??r["usage"]?["completion_tokens"]);
        return input.HasValue&&output.HasValue?(input.Value,output.Value):null;
    }
    public void Record(JsonObject response,string source)
    {
        var numbers=Extract(response);if(numbers==null)return;
        var entry=new JsonObject{{"at",DateTimeOffset.UtcNow.ToString("o")},{"input",numbers.Value.Input},{"output",numbers.Value.Output},{"source",source}};
        // No model output, request body, prompts, URLs, or credentials enter the ledger.
        lock(gate)File.AppendAllText(ledger,entry.ToJsonString()+"\n",Encoding.UTF8);
    }
    public JsonObject Usage()
    {
        var cutoff=DateTimeOffset.UtcNow.Date.AddDays(-29);long input=0,output=0,requests=0;var days=new Dictionary<string,long>();
        lock(gate)
        {
            if(File.Exists(ledger))foreach(var line in File.ReadLines(ledger))
            {
                try{var r=JsonNode.Parse(line);if(!DateTimeOffset.TryParse(r?["at"]?.ToString(),out var at)||at<cutoff)continue;var i=Number(r?["input"]);var o=Number(r?["output"]);if(!i.HasValue||!o.HasValue)continue;input+=i.Value;output+=o.Value;requests++;var date=at.UtcDateTime.ToString("yyyy-MM-dd");days[date]=days.GetValueOrDefault(date)+i.Value+o.Value;}catch{}
            }
        }
        return new JsonObject{{"available",true},{"enabled",Enabled},{"inputTokens",input},{"outputTokens",output},{"tokens",input+output},{"requests",requests},{"history",new JsonArray(days.OrderBy(x=>x.Key).Select(x=>(JsonNode)new JsonObject{{"date",x.Key},{"tokens",x.Value}}).ToArray())}};
    }
    public static async Task<JsonObject> SelfTest(string testFolder)
    {
        Directory.CreateDirectory(testFolder);var folder=Path.Combine(testFolder,Guid.NewGuid().ToString());Directory.CreateDirectory(folder);
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:34567");var upstream=builder.Build();
        upstream.Run(async ctx=>{ctx.Response.ContentType="application/x-ndjson";await ctx.Response.WriteAsync("{\"response\":\"fixture-output-do-not-save\",\"done\":false}\n");await ctx.Response.Body.FlushAsync();await ctx.Response.WriteAsync("{\"done\":true,\"prompt_eval_count\":11,\"eval_count\":7}\n");});
        var meter=new OllamaMeter(folder,"http://127.0.0.1:34567");var checks=new JsonObject();
        try {
            await upstream.StartAsync();await meter.SetEnabled(true);using var client=new HttpClient();using var content=new StringContent("{\"model\":\"fixture\",\"prompt\":\"fixture-secret-prompt\",\"stream\":true}",Encoding.UTF8,"application/json");using var response=await client.PostAsync("http://127.0.0.1:11435/api/generate",content);var body=await response.Content.ReadAsStringAsync();var usage=meter.Usage();
            checks["streamForwarded"]=response.IsSuccessStatusCode&&body.Contains("fixture-output-do-not-save")&&body.Contains("prompt_eval_count");checks["realReportedCountsPersisted"]=usage["tokens"]!.GetValue<long>()==18&&usage["requests"]!.GetValue<long>()==1;
            var file=File.ReadAllText(Path.Combine(folder,"ollama-usage.jsonl"));checks["noPromptsOrResponsesStored"]=!file.Contains("fixture-secret-prompt")&&!file.Contains("fixture-output")&&!file.Contains("fixture\"");
            checks["unapprovedEndpointsRejected"]=(int)(await client.GetAsync("http://127.0.0.1:11435/api/delete")).StatusCode==404;
            await meter.SetEnabled(false);checks["meterStopped"]=!meter.Enabled;checks["historySurvivesRestart"]=new OllamaMeter(folder).Usage()["tokens"]!.GetValue<long>()==18;
        }finally{await meter.SetEnabled(false);await upstream.StopAsync();await upstream.DisposeAsync();}
        return new JsonObject{{"passed",checks.All(x=>x.Value!.GetValue<bool>())},{"checks",checks}};
    }
}
