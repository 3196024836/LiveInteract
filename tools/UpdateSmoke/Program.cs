using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using LiveInteract.Core;
using LiveInteract.Desktop;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>隔离更新验收；可传入发布版辅助程序与新旧探针，真实验证进程等待、重启和回滚。</summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var folder = Path.GetFullPath(args[0]);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            throw new IOException("保留已有测试结果，请选择新的输出目录。");
        }
        Directory.CreateDirectory(folder);
        if (args.Length == 3 && args[1] == "--stage-only")
        {
            CheckActualPackage(Path.GetFullPath(args[2]), folder);
            AtomicFile.WriteAllText(Path.Combine(folder, "actual-package-stage.json"),
                JsonConvert.SerializeObject(new { ActualDistributionZipStaged = true, Passed = true }, Formatting.Indented));
            return 0;
        }
        var install = Path.Combine(folder, "install");
        var staging = Path.Combine(folder, "staging");
        Put(install, UpdatePackage.ExecutableName, "old-app");
        Put(install, "使用说明.md", "old-doc");
        Put(install, "payload/mod/LiveInteract.Silksong.dll", "old-mod");
        Put(install, "WebView2Runtime/browser.exe", "bundled-browser");
        Put(install, "data/user.db", "journal");
        Put(install, "rules.json", "custom-rules");
        Put(install, "user-note.txt", "user-file");
        Put(staging, UpdatePackage.ExecutableName, "new-app");
        Put(staging, "使用说明.md", "new-doc");
        Put(staging, "payload/mod/LiveInteract.Silksong.dll", "new-mod");
        Put(staging, "WebView2Runtime/browser.exe", "must-not-copy");
        Put(staging, "data/user.db", "must-not-copy");
        Put(staging, "rules.json", "must-not-copy");
        var executable = UpdateApplier.Apply(staging, install, 0, out var error);
        Check(error == null, "普通更新失败：" + error);
        Check(File.ReadAllText(executable) == "new-app", "主程序未更新");
        Check(File.ReadAllText(Path.Combine(install, "payload/mod/LiveInteract.Silksong.dll")) == "new-mod", "载荷未更新");
        foreach (var item in new[] { ("WebView2Runtime/browser.exe", "bundled-browser"), ("data/user.db", "journal"),
            ("rules.json", "custom-rules"), ("user-note.txt", "user-file") })
        {
            Check(File.ReadAllText(Path.Combine(install, item.Item1)) == item.Item2, "用户文件被覆盖：" + item.Item1);
        }
        var report = new Dictionary<string, object?> { ["SwapApplied"] = true, ["PreservedUserFiles"] = true,
            ["PreservedBundledRuntime"] = true, ["ProcessTestsRun"] = false };
        if (args.Length >= 4)
        {
            var helper = Path.GetFullPath(args[1]);
            var oldProbe = Path.GetFullPath(args[2]);
            var newProbe = Path.GetFullPath(args[3]);
            report["RealProcessUpdate"] = await RealUpdate(folder, helper, oldProbe, newProbe, false,args.Contains("--wrapped-process"));
            report["RealProcessRollback"] = await RealUpdate(folder, helper, oldProbe, newProbe, true,args.Contains("--wrapped-process"));
            report["OldProcessTimeoutBlocked"] = await TimeoutCase(folder, oldProbe, newProbe);
            report["ProcessTestsRun"] = true;
        }
        if (args.Length >= 5)
        {
            CheckActualPackage(Path.GetFullPath(args[4]), folder);
            report["ActualDistributionZipStaged"] = true;
        }
        report["Passed"] = true;
        AtomicFile.WriteAllText(Path.Combine(folder, "update-smoke.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        Console.WriteLine("UpdateSmoke PASS");
        return 0;
    }

    private static void CheckActualPackage(string archive, string folder)
    {
        var expectedVersion=typeof(UpdateApplier).Assembly.GetName().Version!.ToString(3);
        var actual = UpdateService.Stage(archive, expectedVersion, Path.Combine(folder, "actual-distribution"));
        Check(File.Exists(Path.Combine(actual, UpdatePackage.ExecutableName)), "实际发布 ZIP 主程序根目录错误");
        foreach (var file in new[] { "LiveInteract.Silksong.dll", "LiveInteract.Contracts.dll", "AssetHelperLib.dll", "AssetsTools.NET.dll" })
        {
            Check(File.Exists(Path.Combine(actual, "payload", "mod", file)), "实际发布 ZIP 缺少依赖：" + file);
        }
        Check(FileVersionInfo.GetVersionInfo(Path.Combine(actual, UpdatePackage.ExecutableName)).FileVersion == expectedVersion+".0", "实际发布程序版本错误");
    }

    private static async Task<object> RealUpdate(string root, string helperSource, string oldProbe, string newProbe, bool fail,bool wrapped=false)
    {
        var folder = Path.Combine(root, fail ? "process-rollback" : "process-success");
        var install = Path.Combine(folder, "install");
        Put(install, "使用说明.md", "old-doc");
        Put(install, "a.dll", "old-dependency"); Put(install, "z.dll", "locked-original");
        Put(install, "rules.json", "custom-rules");
        var target = Path.Combine(install, UpdatePackage.ExecutableName);
        File.Copy(oldProbe, target);
        var package = Path.Combine(folder, "wrapped-release.zip");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(newProbe, "release/" + UpdatePackage.ExecutableName);
            foreach (var item in new[] { ("使用说明.md", "new-doc"), ("a.dll", "new-dependency"), ("z.dll", "new-dependency") })
            {
                using var writer = new StreamWriter(zip.CreateEntry("release/" + item.Item1).Open()); writer.Write(item.Item2);
            }
        }
        var staging = UpdateService.Stage(package, "0.8.1", folder);
        var helper = Path.Combine(folder, "helper.exe"); File.Copy(helperSource, helper);
        var signal = Path.Combine(folder, "release-old");
        using var old = Start(target, "--hold", signal);
        try
        {
            await Until(() => File.Exists(signal + ".ready"), "旧实例没有启动");
            using var held = fail ? File.Open(Path.Combine(install, "z.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read) : null;
            var waitPid=wrapped?int.Parse(File.ReadAllText(signal+".pid")):old.Id;
            using var apply = Start(helper, "--apply-update", staging, install, waitPid.ToString());
            await Task.Delay(500);
            Check(!apply.HasExited, "辅助进程没有等待旧实例退出");
            Check(File.ReadAllText(Path.Combine(install, "使用说明.md")) == "old-doc", "旧实例运行期间已经替换文件");
            File.WriteAllText(signal, "exit");
            await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await apply.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Check(apply.ExitCode == (fail ? 1 : 0), "更新辅助进程退出码错误：" + apply.ExitCode);
            await Until(() => File.Exists(Path.Combine(install, "update-restarted.json")), "替换后没有实际重启应用");
            var restarted = JObject.Parse(File.ReadAllText(Path.Combine(install, "update-restarted.json")));
            var expected = fail ? "0.8.0" : "0.8.1";
            Check((string?)restarted["Version"] == expected, "重启的不是预期版本：" + restarted);
            var receipt = JObject.Parse(File.ReadAllText(Path.Combine(install, "update-result.json")))["Result"]!;
            Check((bool?)receipt["Success"] == !fail, "更新持久回执错误");
            Check(File.ReadAllText(Path.Combine(install, "rules.json")) == "custom-rules", "自定义规则被改动");
            Check(Directory.Exists(staging) == fail, "失败应保留暂存，成功后应清理");
            if (fail)
            {
                Check((bool?)receipt["RolledBack"] == true, "失败没有回滚");
                Check(File.ReadAllText(Path.Combine(install, "a.dll")) == "old-dependency", "先替换的文件未恢复");
            }
            return new { OldVersion = "0.8.0", RestartedVersion = expected, WaitedForExit = true,
                ExitCode = apply.ExitCode, RolledBack = fail, Receipt = receipt, Passed = true };
        }
        finally
        {
            File.WriteAllText(signal, "exit");
            if (!old.HasExited) { await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
        }
    }

    private static async Task<bool> TimeoutCase(string root, string oldProbe, string newProbe)
    {
        var folder = Path.Combine(root, "timeout");
        var install = Path.Combine(folder, "install"); Directory.CreateDirectory(install);
        var staging = Path.Combine(folder, "staging"); Directory.CreateDirectory(staging);
        var target = Path.Combine(install, UpdatePackage.ExecutableName);
        File.Copy(oldProbe, target); File.Copy(newProbe, Path.Combine(staging, UpdatePackage.ExecutableName));
        var signal = Path.Combine(folder, "release-old");
        using var old = Start(target, "--hold", signal);
        try
        {
            await Until(() => File.Exists(signal + ".ready"), "超时测试旧实例没有启动");
            var result = UpdateInstaller.Apply(staging, install, old.Id, TimeSpan.FromMilliseconds(120));
            Check(!result.Success && !result.CanRestart, "旧实例未退出时不得覆盖或重复启动");
            Check(!old.HasExited, "更新不得强制结束原实例");
            return true;
        }
        finally { File.WriteAllText(signal, "exit"); await old.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(35)); }
    }

    private static Process Start(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(file)! };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        return Process.Start(start) ?? throw new IOException("无法启动：" + file);
    }

    private static async Task Until(Func<bool> condition, string error)
    {
        var end = DateTime.UtcNow.AddSeconds(12);
        while (!condition()) { Check(DateTime.UtcNow < end, error); await Task.Delay(50); }
    }
    private static void Put(string root, string relative, string value)
    {
        var file = UpdatePackage.ResolveInside(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, value);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}
