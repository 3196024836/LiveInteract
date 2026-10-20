using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>在临时假游戏目录测试维护，不修改真正 Steam 游戏或存档。</summary>
public sealed class InstallationTests : IDisposable
{
    // 独立临时根目录，每个用例不共享状态。
    private readonly string root = Path.Combine(Path.GetTempPath(), "LiveInteractTests-" + Guid.NewGuid().ToString("N"));

    /// <summary>准备独立测试根目录。</summary>
    public InstallationTests() => Directory.CreateDirectory(root);

    /// <summary>安装、禁用、启用、卸载均保留其他插件。</summary>
    [Fact]
    public void FullLifecyclePreservesOtherMods()
    {
        var game = Game(); // 假目标目录。
        var payload = Payload(); // 假发行包。
        var installer = new ModInstaller(payload, () => false); // 不检查真实游戏进程。
        installer.Install(game);
        Assert.True(GameDetector.Inspect(game).ModEnabled);
        installer.Disable(game);
        Assert.False(GameDetector.Inspect(game).ModEnabled);
        Assert.True(GameDetector.Inspect(game).ModDisabled);
        installer.Enable(game);
        Put(game, "BepInEx/plugins/Friend/Friend.dll", "keep");
        installer.Uninstall(game);
        Assert.False(GameDetector.Inspect(game).ModEnabled);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(game, "BepInEx/plugins/Friend/Friend.dll")));
        Assert.True(File.Exists(Path.Combine(game, "winhttp.dll")));
    }

    /// <summary>共享加载器同样需要保留给后来安装的 patcher。</summary>
    [Fact]
    public void UninstallPreservesOtherPatcherLoader()
    {
        var game = Game(); var installer = new ModInstaller(Payload(), () => false);
        installer.Install(game); Put(game, "BepInEx/patchers/Friend.dll", "keep-patcher"); installer.Uninstall(game);
        Assert.True(File.Exists(Path.Combine(game, "winhttp.dll")));
        Assert.Equal("keep-patcher", File.ReadAllText(Path.Combine(game, "BepInEx/patchers/Friend.dll")));
    }

    /// <summary>未知加载器和目录外路径均被拒绝。</summary>
    [Fact]
    public void UnknownLoaderAndTraversalAreBlocked()
    {
        var game = Game(); // 假游戏。
        Put(game, "winhttp.dll", "unknown-loader");
        Assert.Throws<InvalidOperationException>(() => new ModInstaller(Payload(), () => false).Install(game));
        Assert.Equal("unknown-loader", File.ReadAllText(Path.Combine(game, "winhttp.dll")));
        Assert.Throws<IOException>(() => ModInstaller.Inside(game, "../outside.txt"));
    }

    /// <summary>游戏运行时不可安装、停用或卸载已加载组件。</summary>
    [Fact]
    public void RunningGameBlocksFileMutation()
    {
        var installer = new ModInstaller(Payload(), () => true); // 模拟运行中。
        var game = Game(); // 假游戏。
        Assert.Throws<InvalidOperationException>(() => installer.Install(game));
        Assert.Throws<InvalidOperationException>(() => installer.Uninstall(game));
        Assert.False(File.Exists(Path.Combine(game, "winhttp.dll")));
    }

    /// <summary>载荷不完整时不能留下部分加载器。</summary>
    [Fact]
    public void IncompletePayloadDoesNotChangeGame()
    {
        var game = Game(); // 假游戏。
        var payload = Payload(); // 待破坏的假包。
        File.Delete(Path.Combine(payload, "mod/LiveInteract.Contracts.dll"));
        Assert.Throws<FileNotFoundException>(() => new ModInstaller(payload, () => false).Install(game));
        Assert.False(File.Exists(Path.Combine(game, "winhttp.dll")));
    }

    /// <summary>恢复旧存档前必须保留当前版本。</summary>
    [Fact]
    public void RestoreKeepsCurrentSaveRecoveryPoint()
    {
        var saves = Path.Combine(root, "saves"); // 假存档。
        Put(saves, "user1.dat", "original");
        var backups = new SaveBackups(saves, Path.Combine(root, "backups"), () => false); // 隔离维护器。
        var snapshot = backups.Create(); // 原始快照。
        Put(saves, "user1.dat", "modified");
        var recovery = backups.Restore(snapshot); // 恢复前的版本仍应存在。
        Assert.Equal("original", File.ReadAllText(Path.Combine(saves, "user1.dat")));
        Assert.Equal("modified", File.ReadAllText(Path.Combine(recovery, "data/user1.dat")));
    }

    /// <summary>运行游戏时不得复制活动存档。</summary>
    [Fact]
    public void BackupRequiresClosedGame()
    {
        var backups = new SaveBackups(Path.Combine(root, "saves"), Path.Combine(root, "backups"), () => true); // 运行中状态。
        Assert.Throws<InvalidOperationException>(() => backups.Create());
    }

    /// <summary>创建只含识别文件的假游戏，不执行 DLL。</summary>
    /// <returns>假游戏目录。</returns>
    private string Game()
    {
        var game = Path.Combine(root, "game"); // 假游戏根目录。
        Put(game, "Hollow Knight Silksong.exe", "fixture");
        Put(game, "Hollow Knight Silksong_Data/Managed/Assembly-CSharp.dll", "fixture");
        return game;
    }

    /// <summary>构造仅用于文件维护的假载荷。</summary>
    /// <returns>载荷目录。</returns>
    private string Payload()
    {
        var payload = Path.Combine(root, "payload"); // 假包根目录。
        foreach (var file in new[] { "mod/LiveInteract.Silksong.dll", "mod/LiveInteract.Contracts.dll", "loader/winhttp.dll", "loader/doorstop_config.ini", "loader/BepInEx/core/BepInEx.dll" })
        {
            Put(payload, file, "fixture");
        }
        return payload;
    }

    /// <summary>写入隔离的假文件。</summary>
    /// <param name="directory">测试根目录。</param>
    /// <param name="relative">相对文件。</param>
    /// <param name="content">测试内容。</param>
    private static void Put(string directory, string relative, string content)
    {
        var file = Path.Combine(directory, relative); // 明确的测试文件。
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    /// <summary>只清理已验证属于本用例的临时目录。</summary>
    public void Dispose()
    {
        var full = Path.GetFullPath(root); // 已命名并确认的测试根目录。
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("LiveInteractTests-")) { throw new IOException("测试目录边界无效。"); }
        Directory.Delete(full, true);
    }
}
