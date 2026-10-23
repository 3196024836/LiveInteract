using System.Diagnostics;
using System.IO;
using System.Windows;
using LiveInteract.Core;
using Newtonsoft.Json.Linq;

namespace LiveInteract.Desktop;

/// <summary>更新界面只协调用户选择、状态和退出，不再直接复制安装文件。</summary>
public partial class MainWindow
{
    private bool updateBusy;
    private readonly CancellationTokenSource updateShutdown = new();
    private static string CurrentVersion() => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private async void CheckUpdateClick(object? sender, RoutedEventArgs e) => await GuardAsync(() => CheckForUpdatesAsync(true));

    private string? UpdateBlockReason() => UpdateReadiness.BlockReason(source != null && source.IsLoaded,
        runtime.Enabled, GameDetector.IsRunning(), runtime.Journal.PendingUnits, runtime.Journal.PendingFrames,
        runtime.Journal.PendingEvents, maintenanceBusy);

    /// <summary>可以随时查询；只有直播、游戏和队列都空闲时才允许重启安装。</summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (updateBusy || closing) { return; }
        updateBusy = true;
        try
        {
            UpdateStatusText.Text = "正在检查更新…";
            var info = await UpdateService.CheckAsync(UpdateService.DefaultManifest, CurrentVersion(), updateShutdown.Token);
            if (info == null)
            {
                UpdateStatusText.Text = "更新源暂无比 v" + CurrentVersion() + " 更高的版本（" + DateTime.Now.ToString("HH:mm") + " 检查）";
                return;
            }
            info = UpdateService.ForInstallation(info, App.InstallDirectory);
            var blocked = UpdateBlockReason();
            UpdateStatusText.Text = "发现 v" + info.Version + (blocked == null ? "，可安装" : " · " + blocked);
            if (blocked != null)
            {
                runtime.Log("更新", UpdateStatusText.Text);
                if (manual) { MessageBox.Show(this, blocked, "暂不重启更新", MessageBoxButton.OK, MessageBoxImage.Information); }
                return;
            }
            var notes = string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n\n更新说明：\n" + info.Notes;
            var question = "发现 v" + info.Version + "（当前 v" + CurrentVersion() + "）。" + notes +
                "\n\n现在下载并重启更新？失败会回滚并保留原因；当前规则、账本、存档和浏览器登录不变。" +
                "\n游戏 Mod 将在下次一键准备时更新。";
            if (MessageBox.Show(this, question, "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                UpdateStatusText.Text = "已跳过 v" + info.Version;
                return;
            }
            await ApplyUpdateAsync(info);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception exception)
        {
            UpdateStatusText.Text = "更新未完成：" + exception.Message;
            runtime.Log("更新失败", exception.ToString(), false);
            if (manual) { MessageBox.Show(this, UpdateStatusText.Text, "更新未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        finally { updateBusy = false; }
    }

    private async Task ApplyUpdateAsync(UpdateInfo info)
    {
        var progress = new Progress<double>(value =>
        {
            if (!closing) { UpdateStatusText.Text = "正在下载并校验更新包 " + (int)(value * 100) + "%"; }
        });
        var zip = await UpdateService.DownloadAsync(info, progress, updateShutdown.Token);
        string staging;
        try
        {
            UpdateStatusText.Text = "正在预检并解压更新包…";
            staging = await Task.Run(() => UpdateService.Stage(zip, info.Version, dataDirectory), updateShutdown.Token);
        }
        finally { File.Delete(zip); }
        updateShutdown.Token.ThrowIfCancellationRequested();
        var blocked = UpdateBlockReason(); // 下载期间可能开始直播或产生队列，退出前再次核验。
        if (blocked != null) { throw new InvalidOperationException(blocked + " 安装尚未执行。"); }
        SavePreferences(true); // 保存失败时主实例继续运行，不启动辅助进程。
        var helper = PrepareUpdateHelper();
        var start = new ProcessStartInfo(helper) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(helper)! };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(staging);
        start.ArgumentList.Add(App.InstallDirectory); start.ArgumentList.Add(Environment.ProcessId.ToString());
        using var worker = Process.Start(start) ?? throw new IOException("无法启动更新辅助程序，未改动安装文件。");
        runtime.Log("更新", "已校验 v" + info.Version + "，正常关闭服务后替换；失败现场在 " + staging);
        Close(); // 走既有异步关闭流程，不能用 Shutdown 跳过队列/账本释放。
    }

    /// <summary>发布包直接复制单文件宿主；开发构建同时带上相邻依赖，避免辅助进程缺 DLL。</summary>
    private static string PrepareUpdateHelper()
    {
        var folder = Path.Combine(Path.GetTempPath(), "LiveInteract-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var source = Environment.ProcessPath ?? throw new IOException("找不到当前程序。");
        var helper = Path.Combine(folder, Path.GetFileName(source));
        File.Copy(source, helper);
        if (File.Exists(Path.ChangeExtension(source, ".deps.json")))
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                if (file == source) { continue; }
                var extension = Path.GetExtension(file);
                if (extension is ".dll" or ".json") { File.Copy(file, Path.Combine(folder, Path.GetFileName(file))); }
            }
            var runtimes = Path.Combine(AppContext.BaseDirectory, "runtimes");
            if (Directory.Exists(runtimes))
            {
                foreach (var file in Directory.EnumerateFiles(runtimes, "*", SearchOption.AllDirectories))
                {
                    var target = Path.Combine(folder, Path.GetRelativePath(AppContext.BaseDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
                }
            }
        }
        return helper;
    }

    /// <summary>重启后把辅助进程的真实结论送入内置控制台，不把“已下载”当成更新成功。</summary>
    private void ShowLastUpdateResult()
    {
        UpdateStatusText.Text = "当前版本 v" + CurrentVersion() + " · 更新源 GitHub";
        var receipt = Path.Combine(App.InstallDirectory, "update-result.json");
        if (!File.Exists(receipt)) { return; }
        try
        {
            var result = JObject.Parse(File.ReadAllText(receipt))["Result"];
            var success = (bool?)result?["Success"] == true;
            var message = success ? "上次程序更新已完成；游戏 Mod 如需更新，请点击一键准备。" :
                "上次更新未完成：" + (string?)result?["Error"] + " 备份：" + (string?)result?["BackupDirectory"];
            UpdateStatusText.Text = message; runtime.Log("更新结果", message, success);
        }
        catch (Exception error) when (error is IOException or Newtonsoft.Json.JsonException)
        {
            runtime.Log("更新结果", "无法读取上次更新回执：" + error.Message, false);
        }
    }

    private void AutoUpdateChanged(object? sender, RoutedEventArgs e)
    {
        settings.AutoCheckUpdates = AutoUpdateCheck.IsChecked == true;
        SavePreferences();
    }
}
