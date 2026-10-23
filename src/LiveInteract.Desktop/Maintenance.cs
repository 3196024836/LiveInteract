using System.Diagnostics;
using System.IO;
using Newtonsoft.Json;
using LiveInteract.Core;

namespace LiveInteract.Desktop;

/// <summary>所有界面和提升权限入口共用的短时维护流程，先备份再部署，不提升主程序或浏览器。</summary>
internal static class Maintenance
{
    /// <summary>Unity 当前用户标准存档位置，不读取 Steam 登录数据。</summary>
    public static string SaveDirectory => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "..", "LocalLow", "Team Cherry", "Hollow Knight Silksong"));

    /// <summary>执行明确确认的维护操作。</summary>
    /// <param name="operation">固定维护操作。</param>
    /// <param name="directory">已确认的游戏目录。</param>
    /// <param name="payload">便携包载荷。</param>
    /// <param name="dataDirectory">当前用户数据目录。</param>
    /// <returns>结果说明。</returns>
    public static string Run(string operation, string directory, string payload, string dataDirectory)
    {
        var installer = new ModInstaller(payload); // 维护的实际执行者。
        var environment = GameDetector.Inspect(directory); // 操作前重新核验目标。
        if (!environment.GameFound) { throw new InvalidOperationException("目标不是完整 Steam 版《丝之歌》目录。"); }
        if (operation is "install" or "enable")
        {
            var backups = new SaveBackups(SaveDirectory, Path.Combine(dataDirectory, "SaveBackups")); // 部署前自动备份。
            backups.Create();
        }
        return operation switch
        {
            "install" => installer.Install(directory), "enable" => installer.Enable(directory),
            "disable" => installer.Disable(directory), "uninstall" => installer.Uninstall(directory),
            _ => throw new InvalidOperationException("未知维护操作。")
        };
    }

    /// <summary>仅在普通权限维护被拒绝后显示 UAC，请求同一用户提升短时维护进程。</summary>
    /// <param name="operation">固定维护操作。</param>
    /// <param name="directory">已确认目标。</param>
    /// <param name="dataDirectory">当前用户结果目录。</param>
    /// <returns>辅助进程报告。</returns>
    public static async Task<string> ElevateAsync(string operation, string directory, string dataDirectory)
    {
        var resultFile = Path.Combine(dataDirectory, "maintenance-result-" + Guid.NewGuid().ToString("N") + ".json"); // 只在当前用户专用目录回传结果。
        var start = new ProcessStartInfo(File.Exists(App.LauncherPath)?App.LauncherPath:Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }; // 不提升浏览器或主界面。
        start.ArgumentList.Add("--maintain"); start.ArgumentList.Add(operation); start.ArgumentList.Add(directory); start.ArgumentList.Add(resultFile);
        using var process = Process.Start(start) ?? throw new IOException("无法启动维护辅助进程。"); // 系统 UAC 由用户处理。
        await process.WaitForExitAsync();
        if (!File.Exists(resultFile)) { throw new IOException("没有收到维护结果；请确认 UAC 使用同一 Windows 用户，或选择可写的 Steam 库。"); }
        var result = JsonConvert.DeserializeObject<MaintenanceResult>(File.ReadAllText(resultFile)) ?? throw new IOException("维护结果无效。"); // 不读取其他账号结果。
        File.Delete(resultFile);
        if (!result.Success) { throw new IOException(result.Message); }
        return result.Message;
    }

    /// <summary>供短时维护辅助入口写入的结果。</summary>
    internal sealed class MaintenanceResult
    {
        /// <summary>操作是否完成。</summary>
        public bool Success { get; set; }
        /// <summary>用户可理解的原因。</summary>
        public string Message { get; set; } = "";
    }
}
