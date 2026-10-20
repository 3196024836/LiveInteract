using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LiveInteract.Core;

/// <summary>游戏环境检测结果，区分安装、加载器和本工具 Mod 状态。</summary>
public sealed record GameEnvironment(string Directory, bool GameFound, bool LoaderFound, bool ModEnabled, bool ModDisabled, bool Running, string BuildId, string Problem);

/// <summary>只读查找 Steam 库与目标游戏，不读取 Steam 账号凭据。</summary>
public static class GameDetector
{
    /// <summary>本工具插件的唯一相对目录。</summary>
    public const string EnabledPath = "BepInEx/plugins/LiveInteract";
    /// <summary>位于插件扫描目录之外的禁用目录。</summary>
    public const string DisabledPath = "BepInEx/disabled/LiveInteract";

    /// <summary>自动检测本机安装，找不到时允许用户在界面选择。</summary>
    /// <returns>检测结果。</returns>
    public static GameEnvironment Detect()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // 已登记 Steam 根目录。
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"); // 只读安装路径。
            if (key?.GetValue("SteamPath") is string steam) { roots.Add(steam); }
        }
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var libraries = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase); // 合并所有库。
        foreach (var root in roots)
        {
            var library = Path.Combine(root, "steamapps", "libraryfolders.vdf"); // Steam 库登记文件。
            if (!File.Exists(library)) { continue; }
            foreach (Match match in Regex.Matches(File.ReadAllText(library), "\"path\"\\s+\"([^\"]+)\""))
            {
                libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\"));
            }
        }
        foreach (var library in libraries)
        {
            var candidate = Inspect(Path.Combine(library, "steamapps", "common", "Hollow Knight Silksong")); // 已知 Steam 安装名。
            if (candidate.GameFound) { return candidate; }
        }
        return new("", false, false, false, false, IsRunning(), "", "未找到 Steam 版《丝之歌》，请选择游戏目录。");
    }

    /// <summary>检查用户指定目录，绝不把另一个 Unity 游戏当成目标。</summary>
    /// <param name="directory">游戏根目录。</param>
    /// <returns>检测结果。</returns>
    public static GameEnvironment Inspect(string directory)
    {
        directory = Path.GetFullPath(directory);
        var found = File.Exists(Path.Combine(directory, "Hollow Knight Silksong.exe")) && File.Exists(Path.Combine(directory, "Hollow Knight Silksong_Data", "Managed", "Assembly-CSharp.dll")); // 双条件识别目标。
        var loader = File.Exists(Path.Combine(directory, "BepInEx", "core", "BepInEx.dll")) && File.Exists(Path.Combine(directory, "winhttp.dll")); // 实际加载器文件。
        var enabled = File.Exists(Path.Combine(directory, EnabledPath, "LiveInteract.Silksong.dll")); // 已启用插件。
        var disabled = File.Exists(Path.Combine(directory, DisabledPath, "LiveInteract.Silksong.dll")); // 已禁用插件。
        var manifest = Path.GetFullPath(Path.Combine(directory, "..", "..", "appmanifest_1030300.acf")); // 本库的 Steam 清单。
        var build = File.Exists(manifest) ? Regex.Match(File.ReadAllText(manifest), "\"buildid\"\\s+\"(\\d+)\"").Groups[1].Value : "未知"; // 未知版本不伪造。
        var problem = !found ? "不是完整的 Windows Steam 版《丝之歌》目录。" : File.Exists(Path.Combine(directory, "winhttp.dll")) && !loader ? "发现未知 winhttp.dll，不能自动覆盖现有加载器。" : ""; // 冲突诊断。
        return new(directory, found, loader, enabled, disabled, IsRunning(), build, problem);
    }

    /// <summary>检测目标游戏进程，不关闭用户游戏。</summary>
    /// <returns>游戏运行中返回真。</returns>
    public static bool IsRunning()
    {
        foreach (var process in Process.GetProcessesByName("Hollow Knight Silksong"))
        {
            process.Dispose();
            return true;
        }
        return false;
    }
}
