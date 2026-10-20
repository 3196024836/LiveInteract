using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json;
using LiveInteract.Contracts;
using LiveInteract.Core;

namespace LiveInteract.Desktop;

/// <summary>原生 Windows 入口，普通界面不请求管理员权限；提供可审计的维护与自检入口。</summary>
public partial class App : Application
{
    // 交互主程序单实例保护。
    private Mutex? singleInstance;

    /// <summary>从便携包或开发产物中定位受控载荷。</summary>
    internal static string PayloadDirectory
    {
        get
        {
            var packaged = Path.Combine(AppContext.BaseDirectory, "payload"); // 正式便携包位置。
            return Directory.Exists(packaged) ? packaged : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/payload"));
        }
    }

    /// <summary>处理正常界面、维护辅助和只读诊断启动。</summary>
    /// <param name="e">命令行参数。</param>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveInteract"); // 当前用户专用数据，不随程序包分享。
        try
        {
            if (e.Args.Length >= 4 && e.Args[0] == "--maintain")
            {
                var resultFile = ModInstaller.Inside(data, Path.GetFileName(e.Args[3])); // 不允许提升入口任意写文件。
                if (!Path.GetFullPath(e.Args[3]).Equals(resultFile, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resultFile).StartsWith("maintenance-result-", StringComparison.Ordinal)) { throw new IOException("维护结果路径无效。"); }
                Directory.CreateDirectory(data);
                var result = new Maintenance.MaintenanceResult(); // 固定维护结果。
                try { result.Message = Maintenance.Run(e.Args[1], e.Args[2], PayloadDirectory, data); result.Success = true; }
                catch (Exception exception) { result.Message = exception.Message; }
                File.WriteAllText(resultFile, JsonConvert.SerializeObject(result)); Shutdown(result.Success ? 0 : 1); return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--doctor")
            {
                var environment = GameDetector.Detect(); // 只读游戏环境。
                var fixedBrowser = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
                string browser; try { browser = CoreWebView2Environment.GetAvailableBrowserVersionString(Directory.Exists(fixedBrowser) ? fixedBrowser : null); } catch (WebView2RuntimeNotFoundException) { browser = "未安装"; }
                var builtin=Path.Combine(AppContext.BaseDirectory,"profiles","丝之歌-弹幕助力.json"); var builtinRules=0; string? builtinError=null; try { builtinRules=ProfileStore.Read(File.ReadAllText(builtin),false).Rules.Count; } catch(Exception error) { builtinError=error.Message; }
                File.WriteAllText(e.Args[1], JsonConvert.SerializeObject(new { environment, BuiltinRules=builtinRules, BuiltinProfileError=builtinError, Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, BrowserRuntime = browser, ActionDefinitions = ActionCatalog.All.Count, PayloadFound = Directory.Exists(PayloadDirectory) }, Formatting.Indented)); Shutdown(); return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--poster")
            {
                // 按当前规则直接生成透明直播图，便于脚本或外部工具复用同一套排版。
                var profileFile = e.Args.Length >= 3 ? Path.GetFullPath(e.Args[2]) : Path.Combine(AppContext.BaseDirectory, "profiles", "丝之歌-弹幕助力.json");
                var model = ProfileStore.Read(File.ReadAllText(profileFile), false);
                PosterRenderer.Render(model, e.Args[1]);
                Shutdown(); return;
            }
            if (e.Args.Length >= 2 && e.Args[0] == "--game-probe")
            {
                await using var bridge = new GamePipeServer(); bridge.Start();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(25); // 只读等待实际游戏加载。
                while (!bridge.IsConnected && DateTimeOffset.UtcNow < deadline) { await Task.Delay(250); }
                ActionResult? result = null; ActionResult? capture = null; ActionResult? catalog = null;
                if (bridge.IsConnected) { result = await bridge.ExecuteAsync(new() { ActionId = "game.status" }, CancellationToken.None); }
                if (bridge.IsConnected && e.Args.Contains("--catalog")) { catalog = await bridge.ExecuteAsync(new() { ActionId = "game.catalog" }, CancellationToken.None); await Task.Delay(800); }
                if (bridge.IsConnected && e.Args.Contains("--capture")) { capture = await bridge.ExecuteAsync(new() { ActionId = "game.capture" }, CancellationToken.None); await Task.Delay(1000); }
                File.WriteAllText(e.Args[1], JsonConvert.SerializeObject(new { bridge.IsConnected, bridge.Status, Result = result, Capture = capture, Catalog = catalog }, Formatting.Indented)); Shutdown(bridge.IsConnected ? 0 : 2); return;
            }
            var preview = e.Args.Length >= 2 && e.Args[0] == "--render-preview"; // 内部界面验收，不占用游戏、直播或真实配置。
            if (preview) { data = Path.Combine(Path.GetFullPath(e.Args[1]), "preview-data"); }
            else
            {
                singleInstance = new Mutex(true, @"Local\LiveInteract.Desktop.v1", out var created);
                if (!created) { MessageBox.Show("直播互动工具箱已经运行，请使用已打开的窗口。", "LiveInteract"); Shutdown(); return; }
            }
            var window = new MainWindow(data, preview); // 原生控制台。
            if (preview) { window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -20000; window.Top = -20000; window.ShowInTaskbar = false; }
            MainWindow = window;
            window.Closed += (_, _) => Shutdown();
            window.Show();
            if (preview)
            {
                await Task.Delay(250); await window.RenderPreviewsAsync(e.Args[1]); window.Close();
            }
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(data); File.WriteAllText(Path.Combine(data, "startup-error.txt"), exception.ToString());
            if (e.Args.Length > 0) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.txt"), exception.ToString()); }
            else { MessageBox.Show("启动未完成：" + exception.Message + "\n\n诊断位置：" + Path.Combine(data, "startup-error.txt"), "LiveInteract", MessageBoxButton.OK, MessageBoxImage.Error); }
            Shutdown(1);
        }
    }

    /// <summary>退出时释放仅属于本进程的单实例锁。</summary>
    /// <param name="e">退出事件。</param>
    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose(); base.OnExit(e);
    }
}
