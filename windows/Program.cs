using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Sentinel;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if(args.Length>1&&args[0]=="--connect-local"){var state=new NativeBackend().Handle("importLocalConnections",new JsonObject()).GetAwaiter().GetResult();File.WriteAllText(args[1],state.ToJsonString());return;}
        if(args.Length>1&&args[0]=="--test-upgrade"){var result=UpgradeTests.Run(Path.GetDirectoryName(Path.GetFullPath(args[1]))!).GetAwaiter().GetResult();File.WriteAllText(args[1],result.ToJsonString());return;}
        if(args.Length > 0 && args[0] == "--self-test") { File.WriteAllText(args.Length>1?args[1]:"sentinel-tests.json",LocalScanner.SelfTest().ToJsonString()); return; }
        if(args.Length > 0 && args[0] == "--diagnose") { File.WriteAllText(args.Length>1?args[1]:"sentinel-diagnostics.json",new LocalScanner().Scan().ToJsonString()); return; }
        if(args.Length>1&&args[0]=="--test-meter"){var report=OllamaMeter.SelfTest(Path.GetDirectoryName(Path.GetFullPath(args[1]))!).GetAwaiter().GetResult();File.WriteAllText(args[1],report.ToJsonString());return;}
        ApplicationConfiguration.Initialize();
        try { Application.Run(new MainForm(args.Length>1&&args[0]=="--verify-webview"?args[1]:null)); }
        catch { MessageBox.Show("Sentinel could not start. Make sure Microsoft Edge WebView2 Runtime is installed, then try again.","Sentinel",MessageBoxButtons.OK,MessageBoxIcon.Error); }
    }
}
internal sealed class MainForm : Form
{
    readonly WebView2 web = new(); readonly NativeBackend backend = new();
    readonly string? testReport;
    public MainForm(string? report=null)
    {
        testReport=report;
        Text="Sentinel"; Size=new Size(1360,900); MinimumSize=new Size(840,650); StartPosition=FormStartPosition.CenterScreen;
        BackColor=Color.FromArgb(9,13,20);web.Dock=DockStyle.Fill;web.DefaultBackgroundColor=BackColor;Controls.Add(web);
        if(testReport!=null){Opacity=0;ShowInTaskbar=false;}
        var icon=Assembly.GetExecutingAssembly().GetManifestResourceStream("web.sentinel.ico"); if(icon!=null)Icon=new Icon(icon);
        Shown+=async (_,_)=>await Initialize();
    }
    async Task Initialize()
    {
        try
        {
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Sentinel");
            var assembly=Assembly.GetExecutingAssembly();
            using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var files=new Dictionary<string,byte[]>();
            foreach(var name in assembly.GetManifestResourceNames().Where(x=>x.StartsWith("web.")).Order())
            { using var stream=assembly.GetManifestResourceStream(name)!;using var ms=new MemoryStream();stream.CopyTo(ms);var bytes=ms.ToArray();files[name[4..]]=bytes;digest.AppendData(bytes); }
            var folder=Path.Combine(root,"ui",Convert.ToHexString(digest.GetHashAndReset())[..16]);Directory.CreateDirectory(folder);
            foreach(var (name,bytes) in files){var target=Path.Combine(folder,name);Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.WriteAllBytes(target,bytes);}
            var env=await CoreWebView2Environment.CreateAsync(null,Path.Combine(root,"WebView2"));
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping("sentinel.local",folder,CoreWebView2HostResourceAccessKind.DenyCors);
            web.CoreWebView2.Settings.AreDevToolsEnabled=false;web.CoreWebView2.Settings.AreDefaultContextMenusEnabled=false;web.CoreWebView2.Settings.IsStatusBarEnabled=false;
            web.CoreWebView2.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            web.CoreWebView2.NewWindowRequested+=(_,e)=> { e.Handled=true; NativeBackend.OpenLink(e.Uri); };
            web.CoreWebView2.NavigationStarting+=(_,e)=> { if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="sentinel.local") {e.Cancel=true;NativeBackend.OpenLink(e.Uri);} };
            web.CoreWebView2.WebMessageReceived+=async (_,e)=>
            {
                if(!Uri.TryCreate(e.Source,UriKind.Absolute,out var source)||source.Host!="sentinel.local"||source.Scheme!="https")return;
                string id="";
                try {var req=JsonNode.Parse(e.WebMessageAsJson)?.AsObject()??throw new Exception();id=req["id"]?.GetValue<string>()??"";
                    if(testReport!=null&&req["method"]?.ToString()=="diagnosticResult"){File.WriteAllText(testReport,req["params"]!.ToJsonString());await using(var capture=File.Create(Path.ChangeExtension(testReport,".png")))await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,capture);Close();return;}
                    var result=await backend.Handle(req["method"]?.GetValue<string>()??"",req["params"]?.AsObject()??new());
                    if(!IsDisposed)web.CoreWebView2.PostWebMessageAsJson(new JsonObject{{"id",id},{"result",result}}.ToJsonString());}
                catch(Exception ex) { if(!IsDisposed)web.CoreWebView2.PostWebMessageAsJson(new JsonObject{{"id",id},{"error",ex is SafeException?ex.Message:"Operation failed. Try again."}}.ToJsonString()); }
            };
            if(testReport!=null)web.CoreWebView2.NavigationCompleted+=async (_,e)=>
            {
                if(!e.IsSuccess){File.WriteAllText(testReport,new JsonObject{{"passed",false},{"error","WebView navigation failed"}}.ToJsonString());Close();return;}
                await web.CoreWebView2.ExecuteScriptAsync("(async()=>{try{const s=await Sentinel.call('refresh');await new Promise(r=>setTimeout(r,100));const checks={webviewLoaded:document.title.includes('Sentinel'),navigationRendered:document.querySelectorAll('.nav-item').length===5,localCardsRendered:document.querySelectorAll('.local-card').length===2,bridgeReturnsWindows:s.platform==='windows',keyMaterialMasked:s.connections.every(c=>!('apiKey' in c)),codexScanAvailable:s.localUsage.codex?.status==='ok',claudeScanAvailable:s.localUsage.claude?.status==='ok',walletOpens:!!document.querySelector('[data-action=wallet]'),logoAssetsLoaded:[...document.querySelectorAll('.provider-mark img')].every(i=>i.complete&&i.naturalWidth>0),reportLogPersists:Array.isArray(s.activityLog)&&s.activityLog.length>0,realProvidersUpdated:s.connections.filter(c=>['coarena','agentrouter','ollama'].includes(c.adapter)).every(c=>s.snapshots[c.id]?.status==='ok')};window.chrome.webview.postMessage({id:'test',method:'diagnosticResult',params:{passed:Object.values(checks).every(Boolean),checks,localTokens:{codex:s.localUsage.codex?.tokens,claude:s.localUsage.claude?.tokens}}});}catch(e){window.chrome.webview.postMessage({id:'test',method:'diagnosticResult',params:{passed:false,error:e.message}});}})()");
            };
            web.CoreWebView2.Navigate("https://sentinel.local/index.html");
        }
        catch { if(testReport!=null)File.WriteAllText(testReport,new JsonObject{{"passed",false},{"error","WebView2 initialization failed"}}.ToJsonString());else MessageBox.Show("Sentinel needs Microsoft Edge WebView2 Runtime. Install the Evergreen Runtime from Microsoft's WebView2 website, then reopen Sentinel.","Sentinel",MessageBoxButtons.OK,MessageBoxIcon.Information); Close(); }
    }
}
