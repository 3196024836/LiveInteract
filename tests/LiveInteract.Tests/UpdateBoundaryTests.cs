using System.IO.Compression;
using LiveInteract.Core;
using Xunit;

namespace LiveInteract.Tests;

/// <summary>解包、事务和直播安全条件使用相同生产代码。</summary>
public sealed class UpdateBoundaryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "LiveInteract-update-boundary-" + Guid.NewGuid().ToString("N"));
    public UpdateBoundaryTests() { Directory.CreateDirectory(root); }
    public void Dispose() { Directory.Delete(root, true); }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("x/../../outside.txt")]
    [InlineData("x/file:stream")]
    [InlineData("x/CON.txt")]
    [InlineData("x/trailing. ")]
    public void UnsafeZipPathsNeverReachInstallOrExistingStage(string name)
    {
        var zip = Archive(("直播互动工具箱.exe", "app"), (name, "bad"));
        Assert.Throws<InvalidDataException>(() => UpdateService.Stage(zip, "0.8.1", root));
        Assert.False(Directory.Exists(Path.Combine(root, "update")));
    }

    [Fact]
    public void DuplicateNamesAndLinksAreRejected()
    {
        var zip = Archive(("直播互动工具箱.exe", "app"), ("A.dll", "1"), ("a.dll", "2"));
        Assert.Throws<InvalidDataException>(() => UpdateService.Stage(zip, "0.8.1", root));
        zip = Archive(("直播互动工具箱.exe", "app"), ("link", "../target"));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            archive.GetEntry("link")!.ExternalAttributes = unchecked((int)0xA1FF0000);
        }
        Assert.Throws<InvalidDataException>(() => UpdateService.Stage(zip, "0.8.1", root));
    }

    [Fact]
    public void SuccessfulUpdateBacksUpOldFilesAndFailedUpdateRemovesNewFiles()
    {
        var install = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
        File.WriteAllText(Path.Combine(install, UpdatePackage.ExecutableName), "old");
        var stage = UpdateService.Stage(Archive((UpdatePackage.ExecutableName, "new"), ("a.dll", "new-dependency")), "0.8.1", root);
        var result = UpdateInstaller.Apply(stage, install);
        Assert.True(result.Success); Assert.True(result.CanRestart);
        Assert.Equal("new", File.ReadAllText(result.Executable));
        Assert.Equal("old", File.ReadAllText(Path.Combine(result.BackupDirectory, "files", UpdatePackage.ExecutableName)));
        Assert.True(File.Exists(Path.Combine(result.BackupDirectory, "transaction.json")));
        Assert.True(File.Exists(Path.Combine(install, "update-result.json")));
        File.Delete(Path.Combine(install, "a.dll"));
        File.WriteAllText(Path.Combine(install, "z.dll"), "locked-original");
        stage = UpdateService.Stage(Archive((UpdatePackage.ExecutableName, "never"), ("a.dll", "introduced"), ("z.dll", "locked-next")), "0.8.2", root);
        using (File.Open(Path.Combine(install, "z.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            result = UpdateInstaller.Apply(stage, install);
            Assert.False(result.Success); Assert.True(result.RolledBack);
            Assert.False(File.Exists(Path.Combine(install, "a.dll")));
            Assert.Equal("new", File.ReadAllText(result.Executable));
        }
    }

    [Fact]
    public void RunningSelfOrNestedStageCannotOverwriteInstallation()
    {
        var install = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
        File.WriteAllText(Path.Combine(install, UpdatePackage.ExecutableName), "old");
        var stage = UpdateService.Stage(Archive((UpdatePackage.ExecutableName, "new")), "0.8.1", root);
        var result = UpdateInstaller.Apply(stage, install, Environment.ProcessId, TimeSpan.Zero);
        Assert.False(result.Success); Assert.False(result.CanRestart);
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, UpdatePackage.ExecutableName)));
        var nested = Directory.CreateDirectory(Path.Combine(install, "stage")).FullName;
        File.WriteAllText(Path.Combine(nested, UpdatePackage.ExecutableName), "bad");
        Assert.False(UpdateInstaller.Apply(nested, install).Success);
    }

    [Theory]
    [InlineData(true, false, false, 0, 0, 0, false)]
    [InlineData(false, true, false, 0, 0, 0, false)]
    [InlineData(false, false, true, 0, 0, 0, false)]
    [InlineData(false, false, false, 1, 0, 0, false)]
    [InlineData(false, false, false, 0, 1, 0, false)]
    [InlineData(false, false, false, 0, 0, 1, false)]
    [InlineData(false, false, false, 0, 0, 0, true)]
    public void RestartBlockedWhileLiveOrUnfinished(bool source, bool enabled, bool game, long jobs, long frames, long events, bool maintenance)
        => Assert.NotNull(UpdateReadiness.BlockReason(source, enabled, game, jobs, frames, events, maintenance));

    [Fact]
    public void OnlyIdleUpdateCanRestartAndEquivalentVersionDoesNotUpdate()
    {
        Assert.Null(UpdateReadiness.BlockReason(false, false, false, 0, 0, 0, false));
        Assert.False(UpdateService.IsNewer("0.8.1.0", "0.8.1"));
        Assert.False(UpdateService.IsNewer("0.8.1", "0.8.1.0"));
    }

    [Fact]
    public void FullInstallationCannotSilentlyBecomeRuntimeDependent()
    {
        var info = new UpdateInfo { Version = "0.8.1", Url = "https://example.invalid/slim", Size = 1,
            Sha256 = new string('0', 64) };
        Assert.Same(info, UpdateService.ForInstallation(info, root));
        File.WriteAllText(Path.Combine(root, "hostfxr.dll"), "bundled-runtime");
        Assert.Throws<InvalidDataException>(() => UpdateService.ForInstallation(info, root));
        info.FullPackage = new() { Url = "https://example.invalid/full", Size = 2, Sha256 = new string('1', 64) };
        var selected = UpdateService.ForInstallation(info, root);
        Assert.Equal(info.FullPackage.Url, selected.Url);
        Assert.Equal(info.FullPackage.Sha256, selected.Sha256);
    }

    private string Archive(params (string Name, string Content)[] entries)
    {
        var zip = Path.Combine(root, Guid.NewGuid() + ".zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry.Name).Open()); writer.Write(entry.Content);
        }
        return zip;
    }
}
