using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using LiveInteract.Contracts;
using LiveInteract.Core;
using LiveInteract.Desktop;
using Newtonsoft.Json;

/// <summary>实际操作本应用自身的筛选与即时错误提示，不连接游戏和直播。</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
        var app = new App(); app.InitializeComponent(); var result = new Dictionary<string, object?>();
        var main = new MainWindow(Path.Combine(folder, "isolated-ui"), true) { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        main.Loaded += async (_, _) =>
        {
            try
            {
                var runtime = Field<LiveRuntime>(main, "runtime");
                foreach (var type in new[] { LiveEventType.Chat, LiveEventType.Gift, LiveEventType.Like })
                {
                    var input = new LiveEvent { Id = type.ToString(), Nickname = type == LiveEventType.Gift ? "礼物观众" : "其他观众", Type = type, Text = "测试输入" };
                    runtime.Journal.AdmitEvents([input], false); runtime.Journal.CommitDecision(input, [new RuleMatch(new() { Name = "界面验证", Root = new() { ActionId = "health.add" } }, 3)], "");
                }
                Field<CheckBox>(main, "QueueAutoRefresh").IsChecked = false;
                Field<ComboBox>(main, "QueueSourceFilter").SelectedValue = (int)LiveEventType.Gift;
                Refresh(main); Check(Field<DataGrid>(main, "QueueGrid").Items.Count == 1, "来源筛选失败"); result["SourceFilter"] = true;
                var gift = runtime.Journal.QueryJobs(LiveEventType.Gift).Single(); runtime.Journal.Defer(gift.Id, "测试等待原因");
                Field<ComboBox>(main, "QueueStateFilter").SelectedValue = "Waiting"; Refresh(main); Check(Field<DataGrid>(main, "QueueGrid").Items.Count == 1, "状态筛选失败"); result["StateFilter"] = true;
                Field<TextBox>(main, "QueueSearch").Text = "不存在的观众"; Refresh(main); Check(Field<DataGrid>(main, "QueueGrid").Items.Count == 0, "搜索筛选失败");
                Field<TextBox>(main, "QueueSearch").Text = "礼物观众"; Refresh(main); Check(Field<DataGrid>(main, "QueueGrid").Items.Count == 1, "按昵称搜索失败"); result["NicknameSearch"] = true;
                Field<ComboBox>(main, "QueueViewFilter").SelectedValue = 1; Refresh(main); Check(Field<DataGrid>(main, "LogGrid").Items.Count == 1, "输入消息筛选失败"); result["InputView"] = true;
                runtime.Log("测试错误", "本机显式故障原因", false);
                await Task.Delay(450); Check(Field<Border>(main, "ConsoleErrorBanner").Visibility == Visibility.Visible && Field<TextBlock>(main, "ConsoleErrorText").Text.Contains("本机显式故障原因"), "即时错误横幅失败"); result["ImmediateErrorReason"] = true;
                Check(runtime.Journal.QueryJobs().Sum(job => job.Remaining) == 9, "显示筛选修改了真实队列"); result["FilteringDoesNotConsumeQueue"] = true; result["Passed"] = true;
            }
            catch (Exception exception) { result["Passed"] = false; result["Error"] = exception.ToString(); }
            finally { File.WriteAllText(Path.Combine(folder, "queue-ui-smoke.json"), JsonConvert.SerializeObject(result, Formatting.Indented)); main.Close(); }
        };
        main.ShowDialog(); app.Shutdown(); Environment.ExitCode = result.GetValueOrDefault("Passed") is true ? 0 : 1;
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(target)!;
    private static void Refresh(object target) => target.GetType().GetMethod("RefreshQueueView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
    private static void Check(bool condition, string reason) { if (!condition) { throw new InvalidOperationException(reason); } }
}
