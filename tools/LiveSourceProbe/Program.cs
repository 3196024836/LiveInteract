using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LiveInteract.Contracts;
using LiveInteract.Core;
using LiveInteract.Desktop;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>只读连接用户已配置的公开直播间，使用原内置浏览器环境，不读取 Cookie、不发消息、不送礼。</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveInteract");
        var report = new Dictionary<string, object?> { ["ReadOnly"] = true, ["GameBridgeStarted"] = false, ["GiftsPurchased"] = false };
        var app = new App(); app.InitializeComponent();
        var runtime = new LiveRuntime(false, Path.Combine(output, "receiver"));
        var errors = new List<string>(); runtime.EntryAdded += entry => { if (!entry.Success) { lock (errors) { errors.Add(entry.Kind + "：" + entry.Message); } } };
        var room = (string?)JObject.Parse(File.ReadAllText(Path.Combine(data, "settings.json")))["Room"] ?? "";
        var type = typeof(MainWindow).Assembly.GetType("LiveInteract.Desktop.DouyinWindow")!;
        var window = (Window)Activator.CreateInstance(type, runtime, Path.Combine(data, "DouyinBrowser"), room)!;
        window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; window.ShowInTaskbar = false;
        window.Loaded += async (_, _) =>
        {
            try
            {
                ((CheckBox)type.GetField("consent", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).IsChecked = true;
                await (Task)type.GetMethod("ConnectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                await Task.Delay(40000);
                var browser = (WebView2)type.GetField("browser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var bodyJson = await browser.ExecuteScriptAsync("(document.body?.innerText || '').slice(0,15000)");
                var body = JsonConvert.DeserializeObject<string>(bodyJson) ?? "";
                var ended = body.Contains("直播已结束") || body.Contains("当前未开播") || body.Contains("暂未开播") || body.Contains("不在直播");
                var verification = browser.CoreWebView2.DocumentTitle.Contains("验证") || body.Contains("请完成验证") || body.Contains("安全验证");
                report["RoomConfigured"] = room.Length > 0; report["PageLoaded"] = body.Length > 100;
                report["LiveState"] = ended ? "Ended" : verification ? "NeedsUserVerification" : "NotConfirmed";
                report["CapturedFrames"] = runtime.CapturedFrames; report["DecodedFrames"] = runtime.DecodedFrames; report["UniqueInputs"] = runtime.Received;
                report["Chat"] = runtime.CountForType(LiveEventType.Chat); report["Gift"] = runtime.CountForType(LiveEventType.Gift); report["Like"] = runtime.CountForType(LiveEventType.Like);
                report["ReviewFrames"] = runtime.Journal.ReviewFrames; report["InteractionEnabled"] = runtime.Enabled;
                lock (errors) { report["Diagnostics"] = errors.TakeLast(12).ToArray(); }
            }
            catch (Exception exception) { report["Error"] = exception.ToString(); }
            finally
            {
                window.Close(); await runtime.DisposeAsync();
                File.WriteAllText(Path.Combine(output, "live-source-probe.json"), JsonConvert.SerializeObject(report, Formatting.Indented)); app.Shutdown();
            }
        };
        window.ShowDialog();
    }
}
