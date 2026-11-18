using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sentinel;
internal static class OpenRouterSignIn
{
    static string B64(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    public static async Task<string> Authorize(CancellationToken cancellation,Action<string>? opener=null,Func<string,string,Task<string>>? testExchange=null)
    {
        var verifier=B64(RandomNumberGenerator.GetBytes(32));var challenge=B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));var state=B64(RandomNumberGenerator.GetBytes(32));
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();builder.WebHost.UseKestrel(o=>o.Listen(IPAddress.Loopback,0));await using var app=builder.Build();
        var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/callback",async context=>
        {
            context.Response.Headers.CacheControl="no-store";context.Response.Headers.ContentSecurityPolicy="default-src 'none'; style-src 'unsafe-inline'";context.Response.ContentType="text/html";
            if(context.Request.Query["state"]!=state){context.Response.StatusCode=400;await context.Response.WriteAsync("Invalid authorization response.");return;}
            var code=context.Request.Query["code"].ToString();if(code.Length is <1 or >4096||code.Any(char.IsControl)){await context.Response.WriteAsync("Authorization was not completed. Return to Sentinel.");return;}
            completion.TrySetResult(code);await context.Response.WriteAsync("<!doctype html><title>Sentinel connected</title><h2>Authorization received</h2><p>Return to Sentinel. You can close this tab.</p>");
        });
        await app.StartAsync(cancellation);
        try{
            var address=app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();var callback=address+"/callback";
            (opener??NativeBackend.OpenLink)("https://openrouter.ai/auth?callback_url="+Uri.EscapeDataString(callback)+"&code_challenge="+challenge+"&code_challenge_method=S256&state="+state+"&key_label=Sentinel&limit=1&expiration=30-days");
            var code=await completion.Task.WaitAsync(TimeSpan.FromMinutes(5),cancellation);
            if(testExchange!=null)return await testExchange(code,verifier);
            using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false,UseProxy=false}){Timeout=TimeSpan.FromSeconds(30)};
            using var response=await http.PostAsync("https://openrouter.ai/api/v1/auth/keys",new StringContent(new JsonObject{{"code",code},{"code_verifier",verifier},{"code_challenge_method","S256"}}.ToJsonString(),Encoding.UTF8,"application/json"),cancellation);
            if(!response.IsSuccessStatusCode)throw new SafeException("OpenRouter authorization was rejected. Start sign-in again.");
            var bytes=await response.Content.ReadAsByteArrayAsync(cancellation);if(bytes.Length>65536)throw new SafeException("Unexpected authorization response.");var key=JsonNode.Parse(bytes)?["key"]?.ToString();
            if(string.IsNullOrEmpty(key)||key.Length>4096||key.Any(char.IsControl))throw new SafeException("OpenRouter did not return an API key.");return key;
        }catch(TimeoutException){throw new SafeException("Sign-in timed out. Start again when ready.");}catch(OperationCanceledException){throw new SafeException("Sign-in cancelled.");}catch(HttpRequestException){throw new SafeException("Could not reach OpenRouter for authorization.");}
        finally{await app.StopAsync();}
    }
}
