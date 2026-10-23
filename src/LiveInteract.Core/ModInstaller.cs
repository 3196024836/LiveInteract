using System.IO.Compression;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>记录本工具实际创建的文件，供卸载时保留后来加入或修改的内容。</summary>
public sealed class InstallLedger
{
    /// <summary>部署记录版本。</summary>
    public int Version { get; set; } = 1;
    /// <summary>本工具首次安装时创建的共享文件。</summary>
    public List<OwnedFile> LoaderFiles { get; set; } = [];
}

/// <summary>本工具创建文件时的同步元数据，不删除后来发生变化的文件。</summary>
public sealed class OwnedFile
{
    /// <summary>相对游戏根目录的路径。</summary>
    public string RelativePath { get; set; } = "";
    /// <summary>创建后的文件长度。</summary>
    public long Length { get; set; }
    /// <summary>创建后的最后写入时间。</summary>
    public DateTime LastWriteUtc { get; set; }
}

/// <summary>检测后部署专用 Mod，支持启停与卸载；不覆盖未知加载器或删除其他 Mod。</summary>
public sealed class ModInstaller
{
    // 便携包内只包含允许分发的加载器和本工具 DLL。
    private readonly string payload;
    private readonly Func<bool> gameRunning;
    /// <summary>部署记录相对路径。</summary>
    public const string LedgerPath = "BepInEx/liveinteract-install.json";

    /// <summary>建立安装管理器。</summary>
    /// <param name="payloadDirectory">发行包的 payload 目录。</param>
    /// <param name="gameRunning">测试或实际游戏进程检查。</param>
    public ModInstaller(string payloadDirectory, Func<bool>? gameRunning = null)
    {
        payload = Path.GetFullPath(payloadDirectory);
        this.gameRunning = gameRunning ?? GameDetector.IsRunning;
    }

    /// <summary>在游戏关闭时安装或修复，所有载荷预检通过后才写入游戏。</summary>
    /// <param name="gameDirectory">已确认的游戏目录。</param>
    /// <returns>安装结果说明。</returns>
    public string Install(string gameDirectory)
    {
        var environment = GameDetector.Inspect(gameDirectory); // 原目录实际状态。
        if (!environment.GameFound || environment.Problem.Length > 0) { throw new InvalidOperationException(environment.Problem); }
        RequireClosed();
        var root = environment.Directory; // 明确选中的目标目录。
        var modPayload = Path.Combine(payload, "mod"); // 专用 Mod 载荷。
        var modFiles = new[] { "LiveInteract.Silksong.dll", "LiveInteract.Contracts.dll" }; // 不复制游戏程序集。
        foreach (var file in modFiles)
        {
            if (!File.Exists(Path.Combine(modPayload, file))) { throw new FileNotFoundException("发行包缺少 Mod 文件：" + file); }
        }
        modFiles=modFiles.Concat(new[]{"AssetHelperLib.dll","AssetsTools.NET.dll"}.Where(f=>File.Exists(Path.Combine(modPayload,f)))).ToArray();
        var loaderPayload = Path.Combine(payload, "loader"); // 官方社区加载器。
        var shared = new List<(string Source, string Relative)>(); // 本次需要部署的共享文件。
        if (!environment.LoaderFound)
        {
            if (!File.Exists(Path.Combine(loaderPayload, "winhttp.dll")) || !File.Exists(Path.Combine(loaderPayload, "BepInEx", "core", "BepInEx.dll")))
            {
                throw new FileNotFoundException("发行包缺少完整 BepInEx 加载器，请重新解压正式程序包。");
            }
            shared.Add((Path.Combine(loaderPayload, "winhttp.dll"), "winhttp.dll"));
            shared.Add((Path.Combine(loaderPayload, "doorstop_config.ini"), "doorstop_config.ini"));
            foreach (var file in Directory.EnumerateFiles(Path.Combine(loaderPayload, "BepInEx", "core"), "*", SearchOption.AllDirectories))
            {
                shared.Add((file, Path.GetRelativePath(loaderPayload, file)));
            }
            var config = Path.Combine(loaderPayload, "BepInEx", "config", "BepInEx.cfg"); // 初次加载器配置。
            if (File.Exists(config) && !File.Exists(Inside(root, "BepInEx/config/BepInEx.cfg"))) { shared.Add((config, "BepInEx/config/BepInEx.cfg")); }
        }
        foreach (var item in shared)
        {
            if (!File.Exists(item.Source)) { throw new FileNotFoundException("加载器载荷不完整：" + item.Relative); }
            if (File.Exists(Inside(root, item.Relative))) { throw new IOException("检测到已有文件，未覆盖：" + item.Relative); }
        }

        // 记录安装前状态；旧的本工具文件也保留可恢复副本。
        var ledgerFile = Inside(root, LedgerPath); // 本工具安装记录。
        var ledger = File.Exists(ledgerFile) ? JsonConvert.DeserializeObject<InstallLedger>(File.ReadAllText(ledgerFile)) ?? new() : new(); // 原有所有权记录。
        var changed = new List<(string Target, byte[]? Original)>(); // 本次事务的恢复记录。
        try
        {
            foreach (var item in shared)
            {
                var target = Inside(root, item.Relative); // 已检查边界的目标文件。
                changed.Add((target, null));
                CopyAtomic(item.Source, target);
                var info = new FileInfo(target); // 安装后的同步信息。
                ledger.LoaderFiles.Add(new() { RelativePath = item.Relative, Length = info.Length, LastWriteUtc = info.LastWriteTimeUtc });
            }
            if (environment.ModDisabled && !environment.ModEnabled)
            {
                MoveOwned(root, GameDetector.DisabledPath, GameDetector.EnabledPath);
            }
            foreach (var file in modFiles)
            {
                var target = Inside(root, GameDetector.EnabledPath + "/" + file); // 仅属于本工具的插件。
                var original = File.Exists(target) ? File.ReadAllBytes(target) : null; // 小型 DLL 可恢复副本。
                changed.Add((target, original));
                if (original != null)
                {
                    var backup = Inside(root, "BepInEx/LiveInteractBackups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "/" + file); // 保留用户已有版本。
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.WriteAllBytes(backup, original);
                }
                CopyAtomic(Path.Combine(modPayload, file), target);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ledgerFile)!);
            File.WriteAllText(ledgerFile, JsonConvert.SerializeObject(ledger, Formatting.Indented));
            return environment.LoaderFound ? "专用 Mod 已安装，保留原有加载器和其他 Mod。启动游戏后即可连接。" : "加载器与专用 Mod 已安装。启动游戏后即可连接。";
        }
        catch
        {
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                if (item.Original == null) { if (File.Exists(item.Target)) { File.Delete(item.Target); } }
                else { File.WriteAllBytes(item.Target, item.Original); }
            }
            throw;
        }
    }

    /// <summary>启用已安装的专用 Mod，不启用其他插件。</summary>
    /// <param name="root">游戏目录。</param>
    /// <returns>结果说明。</returns>
    public string Enable(string root)
    {
        RequireClosed();
        if (!File.Exists(Inside(root, GameDetector.DisabledPath + "/LiveInteract.Silksong.dll"))) { return Install(root); }
        MoveOwned(root, GameDetector.DisabledPath, GameDetector.EnabledPath);
        return "Mod 已启用，下次启动游戏生效。";
    }

    /// <summary>移动到插件扫描目录之外实现停用。</summary>
    /// <param name="root">游戏目录。</param>
    /// <returns>结果说明。</returns>
    public string Disable(string root)
    {
        RequireClosed();
        if (Directory.Exists(Inside(root, GameDetector.EnabledPath))) { MoveOwned(root, GameDetector.EnabledPath, GameDetector.DisabledPath); }
        return "专用 Mod 已停用；游戏和其他插件保持不变。";
    }

    /// <summary>卸载本工具文件，发现其他 Mod 或后来修改的共享文件时保留。</summary>
    /// <param name="root">游戏目录。</param>
    /// <returns>卸载和保留说明。</returns>
    public string Uninstall(string root)
    {
        RequireClosed();
        foreach (var relative in new[] { GameDetector.EnabledPath, GameDetector.DisabledPath })
        {
            var directory = Inside(root, relative); // 仅删除精确归属的 DLL，不递归删除未知内容。
            foreach (var file in new[] { "LiveInteract.Silksong.dll", "LiveInteract.Contracts.dll", "AssetHelperLib.dll", "AssetsTools.NET.dll" })
            {
                var target = Inside(root, relative + "/" + file); // 已校验目标。
                if (File.Exists(target)) { File.Delete(target); }
            }
            RemoveEmptyParents(directory, root);
        }
        var pluginRoots = new[] { "BepInEx/plugins", "BepInEx/patchers", "BepInEx/disabled" }.Select(relative => Inside(root, relative)); // 包括第三方 patcher 和停用插件。
        var others = pluginRoots.Any(directory => Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Any()); // 不删除其他插件仍需的共享加载器。
        var ledgerFile = Inside(root, LedgerPath); // 本工具记录。
        var preserved = 0; // 保留的共享文件数。
        if (File.Exists(ledgerFile))
        {
            var ledger = JsonConvert.DeserializeObject<InstallLedger>(File.ReadAllText(ledgerFile)) ?? new(); // 安装时同步信息。
            if (!others)
            {
                foreach (var file in ledger.LoaderFiles)
                {
                    var target = Inside(root, file.RelativePath); // 防止记录被改成目录外路径。
                    if (!File.Exists(target)) { continue; }
                    var info = new FileInfo(target); // 当前同步信息。
                    if (info.Length == file.Length && info.LastWriteTimeUtc == file.LastWriteUtc)
                    {
                        File.Delete(target);
                        RemoveEmptyParents(Path.GetDirectoryName(target)!, root);
                    }
                    else { preserved++; }
                }
            }
            File.Delete(ledgerFile);
        }
        return "专用 Mod 已卸载。" + (others ? "检测到其他 Mod，已保留共享加载器。" : "") + (preserved > 0 ? "已保留 " + preserved + " 个安装后发生变化的配置或加载器文件。" : "") + "存档和备份未删除。";
    }

    /// <summary>拒绝运行中修改已加载的 Mod。</summary>
    private void RequireClosed()
    {
        if (gameRunning()) { throw new InvalidOperationException("游戏仍在运行。可在界面安排退出游戏后自动维护，不会强制关闭游戏。"); }
    }

    /// <summary>移动前确认两个路径都处于用户指定的游戏目录且目标不存在。</summary>
    /// <param name="root">明确目标目录。</param>
    /// <param name="from">源相对目录。</param>
    /// <param name="to">目标相对目录。</param>
    private static void MoveOwned(string root, string from, string to)
    {
        var source = Inside(root, from); // 已校验源。
        var target = Inside(root, to); // 已校验目标。
        if (Directory.Exists(target)) { throw new IOException("启用和停用目录同时存在，请先检查本工具备份，未覆盖已有文件。"); }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.Move(source, target);
    }

    /// <summary>拒绝路径穿越和目标子目录中的重解析跳转。</summary>
    /// <param name="root">用户明确指定的根目录。</param>
    /// <param name="relative">受控相对路径。</param>
    /// <returns>经过边界检查的绝对路径。</returns>
    public static string Inside(string root, string relative)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var target = Path.GetFullPath(Path.Combine(root, relative)); // 正规化后的实际目标。
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { throw new IOException("拒绝访问游戏目录之外的路径。"); }
        if ((File.Exists(target) || Directory.Exists(target)) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) { throw new IOException("目标本身是链接，未执行文件操作。"); }
        var current = Path.GetDirectoryName(target); // 逐级检查路径，拒绝子目录跳转到外部。
        while (current != null && current.Length > root.Length)
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) { throw new IOException("目标子目录包含链接，未执行文件操作。"); }
            current = Path.GetDirectoryName(current);
        }
        return target;
    }

    /// <summary>用临时文件完成单个文件替换。</summary>
    /// <param name="source">载荷文件。</param>
    /// <param name="target">经过边界验证的目标。</param>
    private static void CopyAtomic(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".liveinteract-tmp"; // 同目录替换文件。
        if (File.Exists(temporary)) { throw new IOException("存在上次未完成的临时文件，请在界面查看诊断：" + temporary); }
        try { File.Copy(source, temporary); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    /// <summary>仅逐级清理空目录，绝不执行递归删除。</summary>
    /// <param name="directory">候选空目录。</param>
    /// <param name="root">不可删除的游戏根目录。</param>
    private static void RemoveEmptyParents(string directory, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (directory.Length > root.Length && directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) { break; }
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory)!;
        }
    }
}
