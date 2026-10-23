using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace LiveInteract.Desktop;

/// <summary>运行时修复交给不依赖.NET10的发行入口，在软件内部完成，不打开下载网页。</summary>
internal static class RuntimeComponents
{
    public static async Task EnsureWebViewAsync()
    {
        var metadata=Path.Combine(App.InstallDirectory,"distribution.json");
        if(!File.Exists(App.LauncherPath)||!File.Exists(metadata)||
            (bool?)JObject.Parse(File.ReadAllText(metadata))["Bootstrap"]!=true)
        {
            throw new InvalidOperationException("此开发输出没有内置安装入口，请运行精简发行包中的直播互动工具箱.exe。");
        }
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"LiveInteract");
        Directory.CreateDirectory(directory);
        var resultFile=Path.Combine(directory,"component-result-"+Guid.NewGuid().ToString("N")+".json");
        var start=new ProcessStartInfo(App.LauncherPath){UseShellExecute=false,WorkingDirectory=App.InstallDirectory};
        start.ArgumentList.Add("--install-webview");start.ArgumentList.Add(resultFile);
        using var process=Process.Start(start)??throw new IOException("无法启动内置组件安装器。");
        await process.WaitForExitAsync();
        if(!File.Exists(resultFile))throw new IOException("组件准备未完成，可在软件内重试。");
        var result=JObject.Parse(File.ReadAllText(resultFile));
        if((bool?)result["Success"]!=true||process.ExitCode!=0)
            throw new IOException((string?)result["Error"]??"浏览器组件尚未准备完成。");
        File.Delete(resultFile);
    }
}
