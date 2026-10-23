using System.IO.Compression;
using LiveInteract.Core;
using LiveInteract.Desktop;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>覆盖真正的发布目录和失败恢复，不以复制两个文本文件代替更新验收。</summary>
public sealed class UpdateRegressionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LiveInteract-update-tests-" + Guid.NewGuid().ToString("N"));
    public UpdateRegressionTests() { Directory.CreateDirectory(root); }
    public void Dispose() { Directory.Delete(root, true); }

    [Fact]
    public void WrappedReleaseStagesItsExecutableAtReturnedRoot()
    {
        var zip = Archive("LiveInteract-0.8.1/直播互动工具箱.exe", "new");
        var staged = UpdateService.Stage(zip, "0.8.1", Path.Combine(root, "data"));
        Assert.True(File.Exists(Path.Combine(staged, "直播互动工具箱.exe")));
    }

    [Fact]
    public void InvalidVersionCannotEscapeAndDeleteExistingFolder()
    {
        var existing = Path.Combine(root, "data", "keep");
        Directory.CreateDirectory(existing); File.WriteAllText(Path.Combine(existing, "user.txt"), "preserve");
        var error = Record.Exception(() => UpdateService.Stage(Archive("直播互动工具箱.exe", "new"), "../keep", Path.Combine(root, "data")));
        Assert.NotNull(error);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(existing, "user.txt")));
    }

    [Fact]
    public void EmptyPackageIsRejectedWithoutChangingPreviousStage()
    {
        var existing = Path.Combine(root, "data", "update", "0.8.1");
        Directory.CreateDirectory(existing); File.WriteAllText(Path.Combine(existing, "sentinel.txt"), "preserve");
        var zip = Archive("readme.txt", "not a release");
        Assert.ThrowsAny<InvalidDataException>(() => UpdateService.Stage(zip, "0.8.1", Path.Combine(root, "data")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(existing, "sentinel.txt")));
    }

    [Fact]
    public void RollbackRestoresAlreadyReplacedFilesWhenLaterFileIsLocked()
    {
        var install = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
        foreach (var name in new[] { "直播互动工具箱.exe", "a.dll", "z.dll" })
        {
            File.WriteAllText(Path.Combine(install, name), "old");
            File.WriteAllText(Path.Combine(staging, name), "new");
        }
        using (File.Open(Path.Combine(install, "z.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            UpdateApplier.Apply(staging, install, 0, out var error);
            Assert.NotNull(error);
            Assert.Equal("old", File.ReadAllText(Path.Combine(install, "a.dll")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(install, "直播互动工具箱.exe")));
        }
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public void UpdateCannotOverwriteUserRulesDatabaseOrLogin()
    {
        var install = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
        var staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
        foreach (var relative in new[] { "直播互动工具箱.exe", "data/user.db", "current-profile.json", "settings.json", "DouyinBrowser/Cookies", "WebView2Runtime/browser.exe" })
        {
            var old = Path.Combine(install, relative); var next = Path.Combine(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(old)!); Directory.CreateDirectory(Path.GetDirectoryName(next)!);
            File.WriteAllText(old, "user"); File.WriteAllText(next, "package");
        }
        UpdateApplier.Apply(staging, install, 0, out var error);
        Assert.Null(error);
        Assert.Equal("package", File.ReadAllText(Path.Combine(install, "直播互动工具箱.exe")));
        foreach (var relative in new[] { "data/user.db", "current-profile.json", "settings.json", "DouyinBrowser/Cookies", "WebView2Runtime/browser.exe" })
        {
            Assert.Equal("user", File.ReadAllText(Path.Combine(install, relative)));
        }
    }

    private string Archive(string name, string content)
    {
        var zip = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content);
        return zip;
    }
}
