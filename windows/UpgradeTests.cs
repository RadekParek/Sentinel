using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
namespace Sentinel;
internal static class UpgradeTests
{
    public static async Task<JsonObject> Run(string directory)
    {
        var checks=new JsonObject();var root=Path.Combine(directory,"test-vault-"+Guid.NewGuid().ToString("N"));var b=new NativeBackend(root);const string key="synthetic-native-test-key-only-1234";
        var saved=await b.Handle("saveConnection",new JsonObject{{"providerId","openrouter"},{"adapter","openrouter"},{"name","Fixture account"},{"apiKey",key},{"walletGroup","personal"}});var c=saved["connections"]![1]!.AsObject();var id=c["id"]!.ToString();checks["publicStateMasksKey"]=!saved.ToJsonString().Contains(key)&&c["hasKey"]!.GetValue<bool>();
        checks["encryptedAtRest"]=!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root,"vault.bin"))).Contains(key);
        var loaded=await new NativeBackend(root).Handle("getState",new());checks["dpapiReopen"]=loaded["connections"]![1]!["hasKey"]!.GetValue<bool>()&&loaded["connections"]![1]!["walletGroup"]!.ToString()=="personal";
        await b.Handle("saveSnapshots",new JsonObject{{"snapshots",new JsonObject{{id,new JsonObject{{"status","ok"},{"source","Fixture report"},{"checkedAt",DateTime.UtcNow.ToString("O")},{"balanceUsd",12.5},{"tokens",120},{"apiKey",key},{"prompt","NEVER_KEEP_PROMPT"}}}}}});
        var logged=await b.Handle("getState",new());checks["reportLogContainsRealFieldsOnly"]=logged["activityLog"]!.AsArray().Count==1&&logged["activityLog"]![0]!["balanceUsd"]!.GetValue<double>()==12.5&&!logged["activityLog"]!.ToJsonString().Contains(key)&&!logged["activityLog"]!.ToJsonString().Contains("NEVER_KEEP_PROMPT");
        await b.Handle("clearKey",new JsonObject{{"id",id}});var cleared=await new NativeBackend(root).Handle("getState",new());checks["clearKeyPersists"]=!cleared["connections"]![1]!["hasKey"]!.GetValue<bool>()&&!cleared["snapshots"]!.AsObject().ContainsKey(id);
        await b.Handle("clearActivityLog",new());checks["clearReportLogPersists"]=(await new NativeBackend(root).Handle("getState",new()))["activityLog"]!.AsArray().Count==0;
        var urlReady=new TaskCompletionSource<string>();using var cancellation=new CancellationTokenSource(TimeSpan.FromSeconds(15));string? challenge=null;
        var auth=OpenRouterSignIn.Authorize(cancellation.Token,u=>urlReady.SetResult(u),(code,verifier)=>{var computed=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_');checks["pkceVerifierStaysNative"]=computed==challenge&&code=="test-code";return Task.FromResult("fixture-authorized-key");});
        var url=await urlReady.Task;var query=QueryHelpers.ParseQuery(new Uri(url).Query);challenge=query["code_challenge"];checks["oauthUsesOfficialHostAndS256"]=new Uri(url).Host=="openrouter.ai"&&query["code_challenge_method"]=="S256"&&!query.ContainsKey("code_verifier");
        using var http=new HttpClient();var callback=query["callback_url"].ToString();var denied=await http.GetAsync(callback+"?code=test-code&state=wrong");checks["oauthRejectsWrongState"]=(int)denied.StatusCode==400;
        await http.GetAsync(callback+"?code=test-code&state="+query["state"]);checks["loopbackAuthorizationReturnsKey"]=await auth=="fixture-authorized-key";
        return new JsonObject{{"passed",checks.All(x=>x.Value?.ToString()=="true")},{"checks",checks}};
    }
}
