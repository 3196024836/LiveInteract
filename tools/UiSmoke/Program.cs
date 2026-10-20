using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveInteract.Contracts;
using LiveInteract.Core;
using LiveInteract.Desktop;
using Newtonsoft.Json;

/// <summary>在隔离进程中操作真实 WPF 表单，验证简单规则，不启动游戏和直播。</summary>
internal static class Program
{
    /// <summary>执行表单验收并保存读得见的截图。</summary>
    [STAThread]
    private static void Main(string[] args)
    {
        var folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
        var report = new Dictionary<string, object?>();
        try
        {
            var app = new App(); app.InitializeComponent();
            var type = typeof(MainWindow).Assembly.GetType("LiveInteract.Desktop.RuleEditorWindow")!;
            void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
            RuleDefinition Edit(string file, RuleDefinition source, Action<Window> input, bool copying = false)
            {
                var editor = (Window)Activator.CreateInstance(type, source, null)!;
                editor.WindowStartupLocation = WindowStartupLocation.Manual; editor.Left = -20000; editor.Top = -20000; editor.ShowInTaskbar = false;
                Exception? failure = null;
                editor.Loaded += (_, _) => editor.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        Check(!Descendants(editor).OfType<TreeView>().Any(), "仍然存在动作树");
                        Check((bool)type.GetProperty("CreatesNewRule")!.GetValue(editor)! == copying, "旧组合另存状态不正确");
                        input(editor); editor.UpdateLayout(); Capture(editor, Path.Combine(folder, file + ".png"));
                        type.GetMethod("SaveRule", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
                        Check(type.GetProperty("Result")!.GetValue(editor) != null, "有效表单没有保存");
                    }
                    catch (Exception ex) { failure = ex; editor.Close(); }
                }), DispatcherPriority.ContextIdle);
                Check(editor.ShowDialog() == true, "窗口保存失败：" + failure);
                if (failure != null) { throw failure; }
                var result = (RuleDefinition)type.GetProperty("Result")!.GetValue(editor)!;
                Check(result.Root.Kind == StepKind.Action && result.Root.Children.Count == 0 && result.Root.ParameterBindings.Count == 0, "保存的不是单效果");
                ProfileStore.Save(Path.Combine(folder, file + ".json"), new() { Rules = [result] });
                Check(!ProfileStore.Read(File.ReadAllText(Path.Combine(folder, file + ".json"))).Rules[0].Enabled, "导入后没有停用");
                return result;
            }
            var simple = new RuleDefinition { Name = "", Enabled = true, Trigger = new() { Type = LiveEventType.Gift, GiftName = "小心心" }, Root = new() { ActionId = "spawn.bone-goomba" } };
            var spawn = Edit("spawn", simple, editor =>
            {
                Field<TextBox>(type, editor, "effectSearch").Text = "木蜂";
                var list = Field<ListBox>(type, editor, "effectChoice");
                // 搜索按名称模糊匹配：具名怪物与同名原生友军都会出现，必须能选中目标项。
                Check(list.Items.Cast<object>().Any(item => item?.ToString() == "spawn.shellwood-wasp" || (item as ActionDescriptor)?.Id == "spawn.shellwood-wasp"), "搜索没有筛选出具体怪物");
                list.SelectedValue = "spawn.shellwood-wasp";
                var fields = Field<StackPanel>(type, editor, "fields");
                var count = Descendants(fields).OfType<TextBox>().Single(box => box.Tag?.ToString() == "count");
                count.Text = "0"; type.GetMethod("SaveRule", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
                Check(type.GetProperty("Result")!.GetValue(editor) == null, "无效数量被接受");
                count.Text = "3"; Check(Field<TextBlock>(type, editor, "error").Text.Length == 0, "修正输入后仍显示旧错误"); Check(Field<TextBlock>(type, editor, "summary").Text.Contains("生成木蜂 ×3"), "预览数量未同步");
            });
            Check(spawn.Root.ActionId == "spawn.shellwood-wasp" && spawn.Root.Arguments["count"] == "3", "怪物或数量未保存");
            Check(!simple.Root.Arguments.ContainsKey("count"), "编辑污染了原对象");
            report["ConcreteMonsterSearchQuantityPreviewAndValidation"] = true;
            foreach (var sample in new[] { ("healing", "health.add", "7"), ("boss-heal", "boss.heal", "300") })
            {
                var edited = Edit(sample.Item1, simple, editor =>
                {
                    Field<ListBox>(type, editor, "effectChoice").SelectedValue = sample.Item2;
                    var value = Descendants(Field<StackPanel>(type, editor, "fields")).OfType<TextBox>().Single(box => box.Tag?.ToString() == "value"); value.Text = sample.Item3;
                });
                Check(edited.Root.ActionId == sample.Item2 && edited.Root.Arguments["value"] == sample.Item3 && !edited.Root.Arguments.ContainsKey("count"), "切换效果保留了不适用参数");
            }
            report["EditableHealingAndBossHealing"] = true;
            var cast = Edit("silk-skill", simple, editor => Field<ListBox>(type, editor, "effectChoice").SelectedValue = "cast.runerage");
            Check(cast.Root.Arguments.Count == 0, "具名技能仍要求填写技术事件"); report["NamedSilkSkill"] = true;
            var keywordSource = new RuleDefinition { Name = "", Trigger = new() { Type = LiveEventType.Chat, Keywords = ["!spawn/wood"] }, Root = new() { ActionId = "health.full" } };
            var keywordCopy = Edit("keyword", keywordSource, editor => { });
            Check(keywordCopy.Trigger.Keywords.SequenceEqual(new[] { "!spawn/wood" }), "弹幕中的斜线被错误拆分"); report["KeywordLiteralPreserved"] = true;
            var legacy = new RuleDefinition { Name = "原组合", Enabled = true, Root = new() { Kind = StepKind.Sequence, Children = [new() { ActionId = "health.full" }, new() { ActionId = "silk.full" }] } };
            var copy = Edit("legacy-copy", legacy, editor => { }, true);
            Check(copy.Id != legacy.Id && legacy.Root.Kind == StepKind.Sequence && legacy.Root.Children.Count == 2 && !copy.Enabled, "旧规则被覆盖或自动启用"); report["LegacyPreservedAndExplicitCopy"] = true;
            report["Passed"] = true; app.Shutdown();
        }
        catch (Exception exception) { report["Passed"] = false; report["Error"] = exception.ToString(); }
        File.WriteAllText(Path.Combine(folder, "ui-interaction-smoke.json"), JsonConvert.SerializeObject(report, Formatting.Indented));
        Environment.ExitCode = report.GetValueOrDefault("Passed") is true ? 0 : 1;
    }

    /// <summary>读取测试进程自身控件。</summary>
    private static T Field<T>(Type type, object editor, string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

    /// <summary>遍历测试进程自身视图。</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject value)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(value); index++)
        {
            var child = VisualTreeHelper.GetChild(value, index); yield return child;
            foreach (var nested in Descendants(child)) { yield return nested; }
        }
    }

    /// <summary>实际 WPF 渲染，不读取任何其他窗口。</summary>
    private static void Capture(Window window, string file)
    {
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)(content.ActualWidth + content.Margin.Left + content.Margin.Right), (int)(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(file); encoder.Save(output);
    }
}
