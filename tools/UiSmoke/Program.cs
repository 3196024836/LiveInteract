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
        // Keep an outer dispatcher frame alive across successive modal dialog frames.
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>RunChecks(args)));
        Dispatcher.Run();
    }

    private static void RunChecks(string[] args)
    {
        var folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
        var report = new Dictionary<string, object?>();
        try
        {
            var app = new HarnessApp(); app.InitializeComponent(); app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
            if(args.Length>2&&args[1]=="--poster")
            {
                var profile=ProfileStore.Read(File.ReadAllText(Path.GetFullPath(args[2])),false);
                var renderer=typeof(MainWindow).Assembly.GetType("LiveInteract.Desktop.PosterRenderer")!;
                renderer.GetMethod("Render",BindingFlags.Static|BindingFlags.Public)!.Invoke(null,
                    new object[]{profile,Path.Combine(folder,"直播互动说明-透明.png")});
                app.Shutdown();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);return;
            }
            if(args.Length>1&&args[1]=="--clear-hotkey")
            {
                var hotkeyData=Path.Combine(folder,"isolated-hotkey-"+Guid.NewGuid().ToString("N"));
                var main=new MainWindow(hotkeyData,true)
                    {Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false};
                Exception? checkError=null;
                main.Loaded+=(_,_)=>
                {
                    DispatcherTimer? recordTimer=null;
                    try
                    {
                        var settings=Field<AppSettings>(typeof(MainWindow),main,"settings");
                        if(settings.ClearSummonsHotkey!="F8")throw new Exception("Missing F8 default");
                        var hotkeyType=typeof(MainWindow).Assembly.GetType("LiveInteract.Desktop.HotkeyWindow")!;
                        var parse=hotkeyType.GetMethod("Parse",BindingFlags.Public|BindingFlags.Static)!;
                        var f8=((uint Modifiers,uint Key))parse.Invoke(null,new object[]{"F8",true})!;
                        var combo=((uint Modifiers,uint Key))parse.Invoke(null,new object[]{"Ctrl+Alt+F9",true})!;
                        if(f8.Key!=0x77||f8.Modifiers!=0x4000||combo.Modifiers!=0x4003)throw new Exception("Invalid key conversion/no-repeat flags");
                        foreach(var invalid in new[]{("F8",false),("A",true),("Win+F8",true),("Ctrl+NotAKey",true)})
                        {
                            try{parse.Invoke(null,new object[]{invalid.Item1,invalid.Item2});throw new Exception("Invalid key accepted");}
                            catch(TargetInvocationException error)when(error.InnerException is FormatException){}
                        }
                        var beforePause=settings.PauseHotkey;var beforeEmergency=settings.EmergencyHotkey;
                        recordTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
                        recordTimer.Tick+=(_,_)=>
                        {
                            var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.GetType()==hotkeyType);
                            if(dialog==null)return;
                            recordTimer.Stop();
                            Capture(dialog,Path.Combine(folder,"clear-shortcut-dialog.png"));
                            var footer=Field<TextBlock>(typeof(MainWindow),main,"FooterStatus").Text;
                            object[] messageArgs={nint.Zero,0x0312,(nint)14,nint.Zero,false};
                            typeof(MainWindow).GetMethod("HotkeyMessage",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(main,messageArgs);
                            if(!(bool)messageArgs[4]||Field<TextBlock>(typeof(MainWindow),main,"FooterStatus").Text!=footer||!Descendants(dialog).OfType<TextBox>().Any(t=>t.Text=="F8"))
                                throw new Exception("Existing hotkey triggered an action instead of being captured");
                            report["ExistingShortcutCapturedWithoutClearing"]=true;

                            var keyEvent=new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,PresentationSource.FromVisual(dialog),Environment.TickCount,System.Windows.Input.Key.F9)
                                {RoutedEvent=System.Windows.Input.Keyboard.PreviewKeyDownEvent};
                            dialog.RaiseEvent(keyEvent);
                            var save=Descendants(dialog).OfType<Button>().Single(b=>b.Content?.ToString()=="使用这个快捷键");
                            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        };
                        recordTimer.Start();
                        typeof(MainWindow).GetMethod("ChangeHotkeyClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,new object[]{new Button{Tag="clearSummons"},new RoutedEventArgs()});
                        if(settings.ClearSummonsHotkey!="F9"||settings.PauseHotkey!=beforePause||settings.EmergencyHotkey!=beforeEmergency)throw new Exception("Shortcut edit altered another action or did not apply");
                        var saved=AppSettings.Load(Path.Combine(hotkeyData,"settings.json"));
                        if(saved.ClearSummonsHotkey!="F9")throw new Exception("Shortcut not persisted");
                        if(settings.SummonTotalLimit!=40||settings.SummonIntervalSeconds!=.5||settings.SummonQueueCapacity!=200)throw new Exception("Unsafe queue defaults");
                        var runtime=Field<LiveRuntime>(typeof(MainWindow),main,"runtime");
                        var game=runtime.Game;
                        var memory=new MemoryStream();var wire=new StreamWriter(memory,System.Text.Encoding.UTF8,leaveOpen:true){AutoFlush=true};
                        typeof(GamePipeServer).GetField("writer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(game,wire);
                        typeof(GamePipeServer).GetField("lastSeen",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(game,DateTimeOffset.UtcNow);
                        typeof(GamePipeServer).GetProperty("Status")!.SetValue(game,new GameStatus{ModVersion="0.8.5",CanAct=true,SummonCount=5,PendingSummonCount=7,PendingSummonRequests=2,SummonQueuePaused=true,FactionOutlineEnabled=true,FactionOutlineWidth=3.5});
                        if(!settings.FactionOutlineEnabled||settings.FactionOutlineWidth!=2)throw new Exception("Body outline defaults missing");
                        Field<CheckBox>(typeof(MainWindow),main,"GameOverlayCheck").IsChecked=false;
                        Field<CheckBox>(typeof(MainWindow),main,"FactionOutlineCheck").IsChecked=true;
                        Field<TextBox>(typeof(MainWindow),main,"FactionOutlineWidthBox").Text="3.5";
                        Field<TextBox>(typeof(MainWindow),main,"SummonTotalLimitBox").Text="6";
                        Field<TextBox>(typeof(MainWindow),main,"SpawnLimitBox").Text="3";
                        Field<TextBox>(typeof(MainWindow),main,"SummonIntervalBox").Text="0.25";
                        Field<TextBox>(typeof(MainWindow),main,"SummonQueueCapacityBox").Text="12";
                        Field<CheckBox>(typeof(MainWindow),main,"SummonQueuePausedCheck").IsChecked=true;
                        typeof(MainWindow).GetMethod("SaveSettingsClick",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,new object?[]{null,new RoutedEventArgs()});
                        main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
                        var queueSaved=AppSettings.Load(Path.Combine(hotkeyData,"settings.json"));
                        if(queueSaved.GameOverlay||!queueSaved.FactionOutlineEnabled||queueSaved.FactionOutlineWidth!=3.5||queueSaved.SummonTotalLimit!=6||queueSaved.SpawnLimit!=3||queueSaved.SummonIntervalSeconds!=.25||queueSaved.SummonQueueCapacity!=12||!queueSaved.SummonQueuePaused||queueSaved.ClearSummonsHotkey!="F9")throw new Exception("Queue settings did not persist or changed the hotkey");
                        var frames=System.Text.Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF').Split('\n',StringSplitOptions.RemoveEmptyEntries);
                        var payload=frames.Select(line=>JsonConvert.DeserializeObject<BridgeMessage>(line)).Last(frame=>frame?.Kind=="settings")!;
                        var options=Newtonsoft.Json.Linq.JObject.Parse(payload.Text);
                        if((bool?)options["enabled"]!=false||(bool?)options["factionOutlineEnabled"]!=true||(double?)options["factionOutlineWidth"]!=3.5||(int?)options["summonTotalLimit"]!=6||(int?)options["spawnLimit"]!=3||(double?)options["summonIntervalSeconds"]!=.25||(int?)options["summonQueueCapacity"]!=12||(bool?)options["summonQueuePaused"]!=true)throw new Exception("Game settings payload does not match saved queue settings");
                        var refresh=(Task)typeof(MainWindow).GetMethod("RefreshUiAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(main,null)!;
                        main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
                        var queueText=Field<TextBlock>(typeof(MainWindow),main,"SummonQueueStatusText").Text;
                        if(!queueText.Contains("5 / 6")||!queueText.Contains("7 只")||!queueText.Contains("已暂停补位"))throw new Exception("Queue counters were not displayed");
                        report["QueuePausePreservesSettings"]=true;report["QueueSettingsPayload"]=true;report["QueueCountersFromFixtureDisplayed"]=true;
                        report["QueueDefaults40HalfSecond200"]=true;report["AllFourQueueFieldsPersisted"]=true;
                        Field<ListBox>(typeof(MainWindow),main,"Navigation").SelectedIndex=3;
                        main.UpdateLayout();main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
                        var label=Field<TextBlock>(typeof(MainWindow),main,"ClearSummonsHotkeyText");
                        if(!label.Text.StartsWith("F9"))throw new Exception("Shortcut label stale");
                        label.BringIntoView();
                        DependencyObject? ancestor=label;
                        while(ancestor!=null&&ancestor is not ScrollViewer)ancestor=VisualTreeHelper.GetParent(ancestor);
                        if(ancestor is ScrollViewer scroll)scroll.ScrollToEnd();
                        main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
                        main.UpdateLayout();
                        var screenPoint=label.TransformToAncestor(main).Transform(new Point(0,0));
                        if(!label.IsVisible||screenPoint.Y<0||screenPoint.Y>main.ActualHeight-30)throw new Exception("Shortcut row was not visible in settings capture");
                        Capture(main,Path.Combine(folder,"clear-shortcut-settings.png"));
                        var outlineCheck=Field<CheckBox>(typeof(MainWindow),main,"FactionOutlineCheck");
                        outlineCheck.BringIntoView();if(ancestor is ScrollViewer view)view.ScrollToTop();
                        main.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);main.UpdateLayout();
                        var outlineStatus=Field<TextBlock>(typeof(MainWindow),main,"FactionOutlineStatusText").Text;
                        if(!outlineStatus.Contains("本体轮廓已开启")||!outlineStatus.Contains("3.5"))throw new Exception("Effective outline status not displayed");
                        Capture(main,Path.Combine(folder,"body-outline-settings.png"));
                        report["BodyOutlineIndependentFromNamesAndNotifications"]=true;
                        report["BodyOutlineWidthSavedAndSent"]=true;

                        typeof(GamePipeServer).GetField("writer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(game,null);
                        wire.Dispose();memory.Dispose();
                        report["DefaultF8"]=true;report["SingleFunctionKeyAndCombinationParsing"]=true;
                        report["InvalidKeysRejected"]=true;report["RecordingDialogChangedToF9"]=true;
                        report["PersistedAndOtherKeysPreserved"]=true;report["Passed"]=true;
                    }
                    catch(Exception error){checkError=error;report["Passed"]=false;report["Error"]=error.ToString();}
                    finally{recordTimer?.Stop();main.Close();}
                };
                main.ShowDialog();
                File.WriteAllText(Path.Combine(folder,"clear-hotkey-smoke.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
                Environment.ExitCode=checkError==null?0:1;app.Shutdown();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);return;
            }
            if(args.Length>1&&args[1]=="--main-preview")
            {
                app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
                var main=new MainWindow(Path.Combine(folder,"isolated-main"),true)
                    {Left=-20000,Top=-20000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false};
                Exception? renderError=null;
                main.Loaded+=async(_,_)=>
                {
                    try
                    {
                        foreach(var page in new[]{0,1,3,5})
                        {
                            Field<ListBox>(typeof(MainWindow),main,"Navigation").SelectedIndex=page;
                            await main.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
                            main.UpdateLayout();Capture(main,Path.Combine(folder,"main-"+page+".png"));
                            if(Field<TabControl>(typeof(MainWindow),main,"Pages").SelectedIndex!=page)
                                throw new InvalidOperationException("Navigation did not select requested page");
                        }
                        main.Width=1180;main.Height=720;
                        Field<ListBox>(typeof(MainWindow),main,"Navigation").SelectedIndex=0;
                        await main.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ContextIdle);
                        main.UpdateLayout();Capture(main,Path.Combine(folder,"main-minimum.png"));
                    }
                    catch(Exception error){renderError=error;}
                    finally{main.Close();}
                };
                main.ShowDialog();
                if(renderError!=null)throw renderError;
                report["MainPagesRendered"]=true;report["NavigationWorks"]=true;report["Passed"]=true;
                File.WriteAllText(Path.Combine(folder,"ui-interaction-smoke.json"),JsonConvert.SerializeObject(report,Formatting.Indented));
                app.Shutdown();Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);return;
            }

            // Modal editors run under a persistent owner, as in the real application.
            var harness=new Window{Width=1,Height=1,Left=-20000,Top=-20000,ShowInTaskbar=false,Opacity=0,WindowStartupLocation=WindowStartupLocation.Manual};
            app.MainWindow=harness;harness.Show();
            var type = typeof(MainWindow).Assembly.GetType("LiveInteract.Desktop.RuleEditorWindow")!;
            void Check(bool value, string message) { if (!value) { throw new InvalidOperationException(message); } }
            RuleDefinition Edit(string file, RuleDefinition source, Action<Window> input, bool copying = false)
            {
                File.AppendAllText(Path.Combine(folder,"dialog-trace.txt"),file+" create shutdown="+app.Dispatcher.HasShutdownStarted+" mode="+app.ShutdownMode+"\n");
                var editor = (Window)Activator.CreateInstance(type, source, null)!;
                editor.WindowStartupLocation = WindowStartupLocation.Manual; editor.Left = -20000; editor.Top = -20000; editor.ShowInTaskbar = false;
                Exception? failure = null;
                editor.Closed+=(_,_)=>File.AppendAllText(Path.Combine(folder,"dialog-trace.txt"),file+" closed result="+editor.DialogResult+"\n");
                editor.Loaded+=(_,_)=>File.AppendAllText(Path.Combine(folder,"dialog-trace.txt"),file+" loaded\n");
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
                editor.Owner=harness;
                var dialogResult=editor.ShowDialog();
                File.AppendAllText(Path.Combine(folder,"dialog-trace.txt"),file+" return="+dialogResult+" shutdown="+app.Dispatcher.HasShutdownStarted+"\n");
                Check(dialogResult == true, "窗口保存失败：" + failure);
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
                // 同一怪物只出现一次；阵营在参数中选择。
                Check(list.Items.Cast<object>().Any(item => item?.ToString() == "spawn.shellwood-wasp" || (item as ActionDescriptor)?.Id == "spawn.shellwood-wasp"), "搜索没有筛选出具体怪物");
                list.SelectedValue = "spawn.shellwood-wasp";
                var fields = Field<StackPanel>(type, editor, "fields");
                Check(list.Items.Cast<ActionDescriptor>().Count(a=>a.Id=="spawn.shellwood-wasp")==1,"怪物列表重复");
                var faction=Descendants(fields).OfType<ComboBox>().Single(box=>box.Tag?.ToString()=="faction");
                faction.SelectedValue="ally";
                var count = Descendants(fields).OfType<TextBox>().Single(box => box.Tag?.ToString() == "count");
                count.Text = "0"; type.GetMethod("SaveRule", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
                Check(type.GetProperty("Result")!.GetValue(editor) == null, "无效数量被接受");
                count.Text = "3"; Check(Field<TextBlock>(type, editor, "error").Text.Length == 0, "修正输入后仍显示旧错误"); Check(Field<TextBlock>(type, editor, "summary").Text.Contains("生成木蜂 ×3"), "预览数量未同步");
            });
            Check(spawn.Root.ActionId == "spawn.shellwood-wasp" && spawn.Root.Arguments["count"] == "3" && spawn.Root.Arguments["faction"]=="ally", "怪物或数量未保存");
            Check(!simple.Root.Arguments.ContainsKey("count"), "编辑污染了原对象");
            report["ConcreteMonsterSearchQuantityPreviewAndValidation"] = true;
            report["OneMonsterAndFactionDropdownRoundTrip"] = true;
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
        Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
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
