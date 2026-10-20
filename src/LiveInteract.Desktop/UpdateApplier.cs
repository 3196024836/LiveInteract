using System.Diagnostics;
using System.IO;
using LiveInteract.Core;

namespace LiveInteract.Desktop;

/// <summary>在旧进程退出后把暂存目录覆盖到安装目录并重新启动；失败时保留原程序不动。</summary>
public static class UpdateApplier
{
    /// <summary>执行替换；返回需要重新启动的可执行文件路径。</summary>
    public static string Apply(string staging, string install, int waitProcessId, out string? error)
    {
        error = null;
        if (waitProcessId > 0)
        {
            try
            {
                using var process = Process.GetProcessById(waitProcessId);
                process.WaitForExit(30000); // 等旧实例彻底退出再替换文件。
            }
            catch (ArgumentException) { }
        }
        var exe = Path.Combine(install, "直播互动工具箱.exe");
        try
        {
            foreach (var file in Directory.GetFiles(staging, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(staging, file);
                // 完整版自带的浏览器运行时不会被精简包的更新覆盖掉。
                if (relative.StartsWith("WebView2Runtime", StringComparison.OrdinalIgnoreCase)) { continue; }
                var target = Path.Combine(install, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Copy(file, target, true); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(150); } // 少数文件可能仍被释放中。
                }
            }
        }
        catch (Exception exception)
        {
            error = exception.Message;
            try { File.WriteAllText(Path.Combine(install, "update-error.txt"), exception.ToString()); } catch (IOException) { }
        }
        return exe;
    }

    /// <summary>清理不再需要的暂存目录。</summary>
    public static void Cleanup(string staging)
    {
        try { Directory.Delete(staging, true); } catch (IOException) { }
    }
}
