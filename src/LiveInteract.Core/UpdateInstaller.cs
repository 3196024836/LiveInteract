using System.Diagnostics;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>更新结论是独立于重启的持久回执；回滚失败时禁止启动混合版本。</summary>
public sealed record UpdateApplyResult(bool Success, bool CanRestart, string Executable, string? Error,
    string BackupDirectory = "", bool RolledBack = false);

/// <summary>先备份，逐文件原子替换，异常反向回滚；不碰直播账本和游戏目录。</summary>
public static class UpdateInstaller
{
    public static UpdateApplyResult Apply(string staging, string install, int waitProcessId = 0,
        TimeSpan? waitTimeout = null)
    {
        var executable = Path.Combine(Path.GetFullPath(install), UpdatePackage.ExecutableName);
        var backup = "";
        var oldExited = false;
        var lockAcquired = false;
        var replaced = new List<(string Relative, bool Existed)>();
        try
        {
            staging = Path.GetFullPath(staging); install = Path.GetFullPath(install);
            RequireSeparate(staging, install);
            UpdatePackage.RequireRegularPath(staging); UpdatePackage.RequireRegularPath(install);
            WaitForOldProcess(waitProcessId, waitTimeout ?? TimeSpan.FromSeconds(45));
            oldExited = true;
            if (!Directory.Exists(install)) { throw new DirectoryNotFoundException("安装目录不存在。"); }
            using var updateLock = new FileStream(Path.Combine(install, ".liveinteract-update.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            lockAcquired = true;
            var files = ListFiles(staging).Select(f => Path.GetRelativePath(staging, f))
                .Where(UpdatePackage.IsInstallable)
                .OrderBy(f => f.Equals(UpdatePackage.ExecutableName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            if (!files.Contains(UpdatePackage.ExecutableName, StringComparer.OrdinalIgnoreCase) ||
                new FileInfo(Path.Combine(staging, UpdatePackage.ExecutableName)).Length == 0)
            {
                throw new InvalidDataException("暂存目录缺少主程序，未替换任何文件。");
            }
            foreach (var relative in files)
            {
                UpdatePackage.RequireRegularPath(UpdatePackage.ResolveInside(install, relative));
            }
            backup = Path.Combine(Path.GetDirectoryName(staging)!, "backup-" + Guid.NewGuid().ToString("N"));
            UpdatePackage.RequireRegularPath(backup);
            Directory.CreateDirectory(backup);
            var inventory = files.Select(relative => (Relative: relative,
                Existed: File.Exists(UpdatePackage.ResolveInside(install, relative)))).ToArray();
            foreach (var item in inventory.Where(item => item.Existed))
            {
                var destination = UpdatePackage.ResolveInside(backup, "files/" + item.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(UpdatePackage.ResolveInside(install, item.Relative), destination);
            }
            // 完整备份与恢复清单先落盘。保留备份，便于异常断电后人工核对与恢复。
            WriteJson(Path.Combine(backup, "transaction.json"), new { Install = install, Staging = staging,
                At = DateTimeOffset.UtcNow, Files = inventory.Select(x => new { x.Relative, x.Existed }) });
            try
            {
                foreach (var item in inventory)
                {
                    ReplaceFile(UpdatePackage.ResolveInside(staging, item.Relative),
                        UpdatePackage.ResolveInside(install, item.Relative));
                    replaced.Add(item);
                }
            }
            catch (Exception failure)
            {
                var recoveryErrors = new List<string>();
                foreach (var item in replaced.AsEnumerable().Reverse())
                {
                    try
                    {
                        var target = UpdatePackage.ResolveInside(install, item.Relative);
                        if (item.Existed) { ReplaceFile(UpdatePackage.ResolveInside(backup, "files/" + item.Relative), target); }
                        else { File.Delete(target); }
                    }
                    catch (Exception recovery) { recoveryErrors.Add(item.Relative + "：" + recovery.Message); }
                }
                var recovered = recoveryErrors.Count == 0;
                var reason = "更新失败：" + failure.Message + (recovered ? "；已恢复原版本。" :
                    "；回滚未完成，禁止自动重启。" + string.Join("；", recoveryErrors));
                return SaveResult(install, new(false, recovered && File.Exists(executable), executable,
                    reason, backup, recovered));
            }
            return SaveResult(install, new(true, true, executable, null, backup));
        }
        catch (Exception failure)
        {
            return SaveResult(install, new(false, oldExited && lockAcquired && File.Exists(executable), executable,
                "未应用更新：" + failure.Message, backup));
        }
    }

    /// <summary>旧实例未退出或无法确认时不替换；超时不是强制覆盖许可。</summary>
    private static void WaitForOldProcess(int id, TimeSpan timeout)
    {
        if (id <= 0) { return; }
        if (id == Environment.ProcessId) { throw new InvalidOperationException("不能在当前进程运行时替换自身。"); }
        try
        {
            using var process = Process.GetProcessById(id);
            if (!process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue)))
            {
                throw new TimeoutException("旧程序仍在运行，已取消文件替换，请正常关闭旧实例后重试。");
            }
        }
        catch (ArgumentException) { } // 已正常退出，无需再等待。
    }

    private static List<string> ListFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            UpdatePackage.RequireRegularPath(directory);
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                UpdatePackage.RequireRegularPath(file); files.Add(file);
            }
            foreach (var child in Directory.EnumerateDirectories(directory)) { pending.Push(child); }
        }
        return files;
    }

    private static void RequireSeparate(string staging, string install)
    {
        var first = Path.TrimEndingDirectorySeparator(staging) + Path.DirectorySeparatorChar;
        var second = Path.TrimEndingDirectorySeparator(install) + Path.DirectorySeparatorChar;
        if (first.StartsWith(second, StringComparison.OrdinalIgnoreCase) ||
            second.StartsWith(first, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("暂存目录与安装目录不能相同或互相包含。");
        }
    }

    private static void ReplaceFile(string source, string target)
    {
        UpdatePackage.RequireRegularPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".update-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary);
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(temporary, target, true); break; }
                catch (IOException) when (attempt < 5) { Thread.Sleep(100); }
            }
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private static UpdateApplyResult SaveResult(string install, UpdateApplyResult result)
    {
        try { WriteJson(Path.Combine(install, "update-result.json"), new { At = DateTimeOffset.UtcNow, Result = result }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static void WriteJson(string target, object value)
    {
        AtomicFile.WriteAllText(target, JsonConvert.SerializeObject(value, Formatting.Indented));
    }
}
