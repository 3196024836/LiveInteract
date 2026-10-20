using System.IO;
using LiveInteract.Core;
using LiveInteract.Desktop;
using Newtonsoft.Json;

/// <summary>用真实发布包验证热更替换：只覆盖程序与载荷，保留自带浏览器运行时，失败不破坏原目录。</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var folder = Path.GetFullPath(args[0]); if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
        var install = Path.Combine(folder, "install"); Directory.CreateDirectory(install);
        var staging = Path.Combine(folder, "staging");
        var report = new Dictionary<string, object?>();

        File.WriteAllText(Path.Combine(install, "使用说明.md"), "旧版本说明");
        File.WriteAllText(Path.Combine(install, "直播互动工具箱.exe"), "stub");
        Directory.CreateDirectory(Path.Combine(install, "WebView2Runtime")); File.WriteAllText(Path.Combine(install, "WebView2Runtime", "msedgewebview2.exe"), "bundled");
        Directory.CreateDirectory(Path.Combine(install, "payload", "mod")); File.WriteAllText(Path.Combine(install, "payload", "mod", "LiveInteract.Silksong.dll"), "old");
        File.WriteAllText(Path.Combine(install, "user-note.txt"), "用户自己的文件");

        // 临时造一个更新包：说明文件 + 载荷 + 一个 WebView2Runtime 目录（应被忽略）。
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "使用说明.md"), "新版本说明");
        Directory.CreateDirectory(Path.Combine(staging, "payload", "mod")); File.WriteAllText(Path.Combine(staging, "payload", "mod", "LiveInteract.Silksong.dll"), "new");
        Directory.CreateDirectory(Path.Combine(staging, "WebView2Runtime")); File.WriteAllText(Path.Combine(staging, "WebView2Runtime", "msedgewebview2.exe"), "slim-has-none");

        var exe = UpdateApplier.Apply(staging, install, 0, out var error);
        Check(error == null, "替换过程报错：" + error);
        Check(File.ReadAllText(Path.Combine(install, "使用说明.md")) == "新版本说明", "说明文件未更新");
        Check(File.ReadAllText(Path.Combine(install, "payload", "mod", "LiveInteract.Silksong.dll")) == "new", "载荷未更新");
        Check(File.ReadAllText(Path.Combine(install, "WebView2Runtime", "msedgewebview2.exe")) == "bundled", "自带浏览器运行时被覆盖");
        Check(File.ReadAllText(Path.Combine(install, "user-note.txt")) == "用户自己的文件", "用户文件被删改");
        Check(exe.EndsWith("直播互动工具箱.exe", StringComparison.Ordinal), "重启路径不正确");
        report["SwapApplied"] = true;
        report["PreservedBundledRuntime"] = true;
        report["PreservedUserFiles"] = true;

        // 版本比较：远端更新才有更新。
        report["NewerDetected"] = UpdateService.IsNewer("0.7.8", "0.7.7");
        report["SameVersionIgnored"] = !UpdateService.IsNewer("0.7.7", "0.7.7");
        report["Passed"] = true;
        File.WriteAllText(Path.Combine(folder, "update-smoke.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        Console.WriteLine("UpdateSmoke PASS");
        return 0;
    }

    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
}
