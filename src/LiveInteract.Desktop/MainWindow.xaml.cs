using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using LiveInteract.Contracts;
using LiveInteract.Core;

namespace LiveInteract.Desktop;

/// <summary>主播控制台：协调可视化编辑、环境准备、维护和展示，不实现游戏规则本身。</summary>
public partial class MainWindow : Window
{
    // 本机文件与会话服务。
    private readonly string dataDirectory;
    private readonly string settingsFile;
    private readonly string profileFile;
    private readonly bool preview;
    private readonly LiveRuntime runtime;
    private readonly OverlayHost overlay = new();
    private readonly ConcurrentQueue<RuntimeEntry> inbox = new();
    private readonly ObservableCollection<RuntimeEntry> records = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private AppSettings settings = new();
    private RuleProfile profile = new();
    private GameEnvironment environment = new("", false, false, false, false, false, "", "");
    private LiveSourceWindow? source;
    private ActionStep? manual;
    private object? overlayNotice;
    private int ticks;
    private RuntimeEntry? pendingError;
    private int errorDispatchQueued;
    private bool initializing = true, maintenanceBusy, closing, persistentPrompt;
    private HwndSource? hotkeySource;

    /// <summary>建立一体化工具；预览模式使用隔离数据且不占游戏连接。</summary>
    /// <param name="dataDirectory">本用户数据目录。</param>
    /// <param name="preview">是否界面验收模式。</param>
    public MainWindow(string dataDirectory, bool preview = false)
    {
        this.dataDirectory = dataDirectory; this.preview = preview; settingsFile = Path.Combine(dataDirectory, "settings.json"); profileFile = Path.Combine(dataDirectory, "rules.json");
        Directory.CreateDirectory(dataDirectory);
        runtime = new LiveRuntime(!preview, preview ? null : dataDirectory, runWorkers: !preview); runtime.EntryAdded += entry => { inbox.Enqueue(entry); if (!entry.Success) { Interlocked.Exchange(ref pendingError, entry); if (Interlocked.Exchange(ref errorDispatchQueued, 1) == 0) { Dispatcher.BeginInvoke(ShowPendingError, DispatcherPriority.Send); } } };
        runtime.ActionCompleted += (request, result) => Dispatcher.BeginInvoke(() =>
        {
            if (result.Retryable) { return; }
            var title = (request.Triggerer.Length > 0 ? request.Triggerer + " · " : "") + (request.EffectText.Length > 0 ? request.EffectText : ActionPresentation.Describe(request)); // 实际执行动作。
            overlayNotice = new { At = DateTimeOffset.UtcNow, Title = ("") + title, Detail = settings.BrowserShowResult ? (!result.Success ? "执行未完成，主播可在控制台查看原因。" : request.ActionId == "game.capture" ? "已请求游戏截图，保存位置请在控制台查看。" : result.Message) : "", Success = result.Success, Simulation = request.DisplayText.Contains("模拟") };
            LastActionText.Text = title + " · " + result.Message;
        });
        InitializeComponent();
        if (!preview) { Width = Math.Min(1400, SystemParameters.WorkArea.Width - 40); Height = Math.Min(900, SystemParameters.WorkArea.Height - 40); }
        try { settings = AppSettings.Load(settingsFile); }
        catch (JsonException exception) { runtime.Log("配置错误", "偏好格式损坏，原文件保留：" + exception.Message, false); settings = new(); }
        try { if (File.Exists(profileFile)) { profile = ProfileStore.Read(File.ReadAllText(profileFile), false); } else { var builtin=Path.Combine(AppContext.BaseDirectory,"profiles","丝之歌-弹幕助力.json"); if(File.Exists(builtin)){profile=ProfileStore.Read(File.ReadAllText(builtin),false);settings.SpawnLimit=profile.SpawnLimitOverride??settings.SpawnLimit;ProfileStore.Save(profileFile,profile);} } }
        catch (Exception exception) { runtime.Log("配置错误", "规则文件未载入，原文件保留：" + exception.Message, false); }
        runtime.ApplyProfile(profile);
        LogGrid.ItemsSource = records;
        QueueViewFilter.ItemsSource = new[] { new Ui.Pick<int>(0, "执行队列"), new Ui.Pick<int>(1, "收到的消息"), new Ui.Pick<int>(2, "运行日志") }; QueueViewFilter.DisplayMemberPath = "Name"; QueueViewFilter.SelectedValuePath = "Value"; QueueViewFilter.SelectedIndex = 0;
        QueueSourceFilter.ItemsSource = new[] { new Ui.Pick<int>(-1, "全部来源") }.Concat(Ui.EventTypes.Select(pair => new Ui.Pick<int>((int)pair.Value, pair.Name))).ToArray(); QueueSourceFilter.DisplayMemberPath = "Name"; QueueSourceFilter.SelectedValuePath = "Value"; QueueSourceFilter.SelectedIndex = 0;
        QueueStateFilter.ItemsSource = new[] { ("", "全部状态"), ("Queued", "排队中"), ("Waiting", "等待条件"), ("Running", "执行中"), ("Succeeded", "已生效"), ("NoChange", "没有数值变化"), ("Failed", "失败"), ("Unknown", "结果待核对"), ("AwaitingResume", "等待恢复"), ("Cancelled", "已取消") }.Select(pair => new Ui.Pick<string>(pair.Item1, pair.Item2)).ToArray(); QueueStateFilter.DisplayMemberPath = "Name"; QueueStateFilter.SelectedValuePath = "Value"; QueueStateFilter.SelectedIndex = 0;
        SimType.ItemsSource = Ui.EventTypes.Select(item => new Ui.Pick<LiveEventType>(item.Value, item.Name)).ToArray(); SimType.DisplayMemberPath = "Name"; SimType.SelectedValuePath = "Value"; SimType.SelectedIndex = 0;
        CategoryFilter.ItemsSource = new[] { "全部功能" }.Concat(ActionCatalog.UserFacing.Select(item => item.Category).Distinct()).ToList(); CategoryFilter.SelectedIndex = 0;
        CornerBox.ItemsSource = new[] { new Ui.Pick<string>("BottomLeft", "左下"), new("BottomRight", "右下"), new("TopLeft", "左上"), new("TopRight", "右上") }; CornerBox.DisplayMemberPath = "Name"; CornerBox.SelectedValuePath = "Value"; CornerBox.SelectedValue = settings.Corner;
        GameCornerBox.ItemsSource = new[] { new Ui.Pick<string>("BottomLeft", "左下"), new("BottomRight", "右下"), new("TopLeft", "左上"), new("TopRight", "右上") }; GameCornerBox.DisplayMemberPath = "Name"; GameCornerBox.SelectedValuePath = "Value"; GameCornerBox.SelectedValue = settings.GameCorner;
        GameFontBox.Text = settings.GameFontSize.ToString(); GameAccentBox.Text = settings.GameAccent; GameDurationBox.Text = settings.GameNoticeSeconds.ToString(CultureInfo.InvariantCulture); BrowserDurationBox.Text = settings.BrowserNoticeSeconds.ToString(CultureInfo.InvariantCulture);
        BrowserSourceCheck.IsChecked = settings.BrowserShowSource; BrowserResultCheck.IsChecked = settings.BrowserShowResult; BrowserEffectsCheck.IsChecked = settings.BrowserShowEffects; BrowserProgressCheck.IsChecked = settings.BrowserShowProgress;
        GameSourceCheck.IsChecked = settings.GameShowSource; GameResultCheck.IsChecked = settings.GameShowResult; GameEffectsCheck.IsChecked = settings.GameShowEffects; GameProgressCheck.IsChecked = settings.GameShowProgress;
        GamePathBox.Text = settings.GameDirectory; GameOverlayCheck.IsChecked = settings.GameOverlay; BrowserOverlayCheck.IsChecked = settings.BrowserOverlay; HideNamesCheck.IsChecked = settings.HideNicknames;
        OverlayFontBox.Text = settings.FontSize.ToString(); AccentBox.Text = settings.Accent; PortBox.Text = settings.OverlayPort.ToString(); SpawnLimitBox.Text = settings.SpawnLimit.ToString();
        PauseHotkeyText.Text = settings.PauseHotkey + " · 暂停 / 恢复新触发"; EmergencyHotkeyText.Text = settings.EmergencyHotkey + " · 紧急停止与恢复";
        runtime.HideNicknames = false; settings.HideNicknames = false; HideNamesCheck.IsChecked = false; CatalogCountText.Text = ActionCatalog.All.Count + " 项动作定义 · 实际效果逐项验收";
        initializing = false; NavigationChanged(null, null); RefreshRules(); FilterActions(null, null); RefreshEnvironment(true); RefreshBackups();
        timer.Tick += async (_, _) => await RefreshUiAsync(); timer.Start();
        Loaded += async (_, _) =>
        {
            if (!preview)
            {
                RegisterKeys();
                try { await overlay.StartAsync(settings.OverlayPort); OverlayUrlBox.Text = overlay.Url; }
                catch (Exception exception) { runtime.Log("展示服务", "透明源未启动：" + exception.Message + "。可修改端口后重启工具。", false); OverlayUrlBox.Text = "透明源未启动，请查看运行记录。"; }
            }
            else { OverlayUrlBox.Text = "http://127.0.0.1:17573/overlay"; }
            FooterStatus.Text = "本地服务已就绪；互动总开关保持关闭。";
        };
        Closing += OnClosing;
    }

    /// <summary>页面导航只改变当前工作区。</summary>
    /// <param name="sender">导航控件。</param>
    /// <param name="e">选择事件。</param>
    private void NavigationChanged(object? sender, SelectionChangedEventArgs? e)
    {
        if (Pages == null || Navigation == null) { return; }
        Pages.SelectedIndex = Navigation.SelectedIndex; PageTitle.Text = (Navigation.SelectedItem as ListBoxItem)?.Content?.ToString() ?? "直播互动工具箱";
    }

    /// <summary>开始真实互动前自动检查游戏准备，游戏未就绪时提供明确下一步。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void ToggleLive(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (runtime.Enabled) { runtime.SetEnabled(false); return; }
        if (!profile.Rules.Any(item => item.Enabled)) { Navigation.SelectedIndex = 1; MessageBox.Show(this, "先创建并启用至少一条规则，再开启互动。", "还没有启用的规则"); return; }
        if (!await EnsurePreparedAsync()) { return; }
        if (!runtime.Game.IsConnected)
        {
            if (!GameDetector.IsRunning()) { LaunchGame(); }
            FooterStatus.Text = "游戏已准备。请进入可操作场景，看到游戏桥连接后再开启互动。"; return;
        }
        if (source == null) { ConnectRoomClick(this, e); FooterStatus.Text = "先在内置窗口连接本人直播间，确认实际收包后开启。"; return; }
        runtime.SetEnabled(true);
    });

    /// <summary>紧急停止不要求游戏仍可操作。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void EmergencyClick(object? sender, RoutedEventArgs e) => await GuardAsync(runtime.EmergencyStopAsync);

    /// <summary>启动本人登录的独立直播间窗口。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void ConnectRoomClick(object? sender, RoutedEventArgs e)
    {
        if (source != null) { source.Activate(); return; }
        // 沿用原浏览器数据目录，切换版本后不必重新登录。
        source = new LiveSourceWindow(runtime, Path.Combine(dataDirectory, "DouyinBrowser"), settings.Platform, settings.Room, settings.BilibiliRoom) { Owner = this };
        source.SourceStatusChanged += text => Dispatcher.BeginInvoke(() => { SourceStatusText.Text = text.Length > 32 ? text[..32] + "…" : text; SourceStatusText.ToolTip = text; });
        source.SelectionChanged += (platform, room) =>
        {
            settings.Platform = platform;
            if (platform == LiveSourceWindow.BilibiliKey) { settings.BilibiliRoom = room; } else { settings.Room = room; }
            SavePreferences();
        };
        source.Closed += (_, _) => source = null; source.Show();
    }

    /// <summary>提交明确的模拟输入；命中规则后执行真实游戏动作。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void SimulateClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (!long.TryParse(SimCount.Text, out var count) || count < 1 || count > 1000000) { throw new InvalidDataException("测试数量应为 1 至 1000000。"); }
        if (!profile.Rules.Any(item => item.Enabled)) { throw new InvalidOperationException("还没有启用的规则。模拟输入只测试已启用规则。"); }
        if (!await EnsurePreparedAsync()) { return; }
        var type = (LiveEventType)SimType.SelectedValue; // 主播明确选择的输入类型。
        runtime.Submit(new() { Id = "simulation:" + Guid.NewGuid().ToString("N"), RoomId = "simulation", Type = type, UserId = "simulation-user", Nickname = "本机测试", Text = SimText.Text, GiftName = type == LiveEventType.Gift ? SimText.Text : "", Count = count, ComboTotal = count, ComboEnded = true, IsSimulation = true });
    });

    /// <summary>搜索动作目录，只过滤显示而不改变定义。</summary>
    /// <param name="sender">筛选控件。</param>
    /// <param name="e">事件。</param>
    private void FilterActions(object? sender, RoutedEventArgs? e)
    {
        if (initializing || ActionsList == null) { return; }
        var query = ActionSearch.Text.Trim(); // 用户搜索。
        var category = CategoryFilter.SelectedItem?.ToString() ?? "全部功能"; // 分类只用于查找。
        ActionsList.ItemsSource = ActionCatalog.UserFacing.Where(item => (category == "全部功能" || item.Category == category) && (query.Length == 0 || (item.Name + item.Category + item.Description + item.Id).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>为选中动作构造参数表单。</summary>
    /// <param name="sender">动作列表。</param>
    /// <param name="e">事件。</param>
    private void ActionSelected(object? sender, SelectionChangedEventArgs? e)
    {
        if (ActionsList.SelectedItem is not ActionDescriptor descriptor) { return; }
        manual = new() { ActionId = descriptor.Id, DurationSeconds = 10 };
        ActionNameText.Text = descriptor.Name; ActionDescriptionText.Text = descriptor.Description; ActionRiskText.Text = descriptor.IsPersistent ? "永久修改 · 需在设置中单独确认，建议先备份。" : descriptor.IsTemporary ? "临时效果 · 到期、切场景或控制台断开后恢复。" : "即时动作 · 可能影响正常游戏过程。";
        Ui.ActionFields(ManualParameters, manual, runtime.Game.Status); ManualResultText.Text = "";
    }

    /// <summary>手动测试选中动作，不伪造成功结果。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void TestActionClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (manual == null) { return; }
        if (!await EnsurePreparedAsync()) { return; }
        ManualResultText.Text = "正在等待游戏实际回执…";
        ManualResultText.Text = await runtime.TestActionAsync(manual) ? "游戏端已执行，请在实际画面确认效果。" : "未完成执行，请看运行记录中的具体原因。";
    });

    /// <summary>新建自定义规则。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void NewRuleClick(object? sender, RoutedEventArgs e) => EditRule(new() { Name = "我的新规则", Enabled = true, Trigger = new() { Type = LiveEventType.Gift, GiftName = "小心心" }, Root = new() { ActionId = "spawn.bone-goomba" } }, true);

    /// <summary>以当前功能建立规则，不预设观众玩法。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void NewRuleFromActionClick(object? sender, RoutedEventArgs e)
    {
        if (manual == null) { return; }
        EditRule(new() { Name = ActionCatalog.All.First(item => item.Id == manual.ActionId).Name, Root = JsonConvert.DeserializeObject<ActionStep>(JsonConvert.SerializeObject(manual))! }, true);
    }

    /// <summary>编辑选中规则。</summary>
    /// <param name="sender">按钮或列表。</param>
    /// <param name="e">事件。</param>
    private void EditRuleClick(object? sender, RoutedEventArgs e) { if (SelectedRule() is { } rule) { EditRule(rule, false); } }

    /// <summary>复制规则为关闭的新规则。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void CopyRuleClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedRule() is not { } rule) { return; }
        var copy = JsonConvert.DeserializeObject<RuleDefinition>(JsonConvert.SerializeObject(rule))!; // 独立编辑副本。
        copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " · 副本"; copy.Enabled = false; profile.Rules.Add(copy); SaveRules();
    }

    /// <summary>确认后删除单条规则，不操作游戏。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void DeleteRuleClick(object? sender, RoutedEventArgs e)
    {
        if (SelectedRule() is { } rule && MessageBox.Show(this, "删除规则“" + rule.Name + "”？这不会撤销游戏中已执行的修改。", "删除规则", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { profile.Rules.Remove(rule); SaveRules(); }
    }

    /// <summary>切换规则状态，总开关不会被此操作开启。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void RuleToggleClick(object? sender, RoutedEventArgs e) { if (SelectedRule() is { } rule) { rule.Enabled = !rule.Enabled; SaveRules(); } }

    /// <summary>上移规则匹配顺序。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void RuleUpClick(object? sender, RoutedEventArgs e) => MoveRule(-1);

    /// <summary>下移规则匹配顺序。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void RuleDownClick(object? sender, RoutedEventArgs e) => MoveRule(1);

    /// <summary>导入配置时关闭所有规则，不能携带运行许可。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void ImportRulesClick(object? sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        var dialog = new OpenFileDialog { Filter = "互动规则配置 (*.json)|*.json", Title = "导入配置（导入后规则默认关闭）" }; // 用户明确选择文件。
        if (dialog.ShowDialog(this) != true) { return Task.CompletedTask; }
        var imported = ProfileStore.Read(File.ReadAllText(dialog.FileName)); // 先完整验证再替换当前配置。
        if (profile.Rules.Count > 0 && MessageBox.Show(this, "将替换当前规则，原配置会自动备份。继续导入？", "导入规则", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { return Task.CompletedTask; }
        if (File.Exists(profileFile)) { File.Copy(profileFile, profileFile + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")); }
        profile = imported; SaveRules(); return Task.CompletedTask;
    });

    /// <summary>从随包文件选择默认配置；原配置备份，总开关仍关闭。</summary>
    /// <summary>明确确认后采用随包默认方案，不覆盖未备份的用户规则。</summary>
    private async void UseBuiltinProfileClick(object? sender,RoutedEventArgs e)=>await GuardAsync(async()=>
    {
        var file=Path.Combine(AppContext.BaseDirectory,"profiles","丝之歌-弹幕助力.json");
        var candidate=ProfileStore.Read(File.ReadAllText(file),false);
        if(MessageBox.Show(this,"使用内置最新方案？原规则先备份，互动总开关保持关闭。","内置方案",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        runtime.SetEnabled(false);if(File.Exists(profileFile))File.Copy(profileFile,profileFile+".backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        profile=candidate;if(candidate.SpawnLimitOverride.HasValue){settings.SpawnLimit=candidate.SpawnLimitOverride.Value;SpawnLimitBox.Text=settings.SpawnLimit.ToString();settings.Save(settingsFile);await ApplyGameSettingsAsync();}
        SaveRules();Navigation.SelectedIndex=1;
    });
    /// <summary>礼物名称和实际ID集中改绑；每1份触发一次，不叠加累计门槛。</summary>
    private void GiftBindingsClick(object? sender,RoutedEventArgs e)
    {
        var window=new GiftBindingsWindow(profile.Rules,records.Where(r=>r.SourceEvent?.Type==LiveEventType.Gift).Select(r=>r.SourceEvent!.GiftName)){Owner=this};
        if(window.ShowDialog()!=true||window.Result==null)return;
        runtime.SetEnabled(false);if(File.Exists(profileFile))File.Copy(profileFile,profileFile+".backup-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        foreach(var rule in profile.Rules.Where(r=>window.Result.ContainsKey(r.Id))){var binding=window.Result[rule.Id];rule.Trigger.GiftName=binding.Name;rule.Trigger.GiftId=binding.Id;rule.Trigger.GiftNameAliases.Clear();rule.Trigger.Threshold=1;rule.Trigger.Counting=TriggerCounting.PerEvent;rule.Enabled=true;}
        SaveRules();
    }

    private async void LoadDefaultProfileClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "profiles");
        var dialog = new OpenFileDialog { Filter = "互动规则配置 (*.json)|*.json", Title = "选择默认 / 自定义配置文件", InitialDirectory = Directory.Exists(directory) ? directory : dataDirectory, FileName = "丝之歌-弹幕助力.json" };
        if (dialog.ShowDialog(this) != true) { return; }
        var candidate = ProfileStore.Read(File.ReadAllText(dialog.FileName), false);
        if (MessageBox.Show(this, "载入“" + candidate.Name + "”并采用文件中的规则启用状态？\n\n原配置会先备份，互动总开关保持关闭。已有队列仍使用收到时的配置。\n本文件的同时生成上限设置也会应用（0＝不设总上限）。", "读取默认配置", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { return; }
        runtime.SetEnabled(false);
        if (File.Exists(profileFile)) { File.Copy(profileFile, profileFile + ".backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")); }
        if (candidate.SpawnLimitOverride.HasValue) { settings.SpawnLimit = candidate.SpawnLimitOverride.Value; SpawnLimitBox.Text = settings.SpawnLimit.ToString(); settings.Save(settingsFile); await ApplyGameSettingsAsync(); }
        profile = candidate; SaveRules(); Navigation.SelectedIndex = 1; return;
    });

    /// <summary>来源 / 状态 / 搜索只改变界面，不改变排队数据。</summary>
    private void QueueFilterChanged(object? sender, RoutedEventArgs e)
    {
        if (!initializing && QueueGrid != null) { RefreshQueueView(); }
    }
    private void RefreshQueueView()
    {
        var mode = QueueViewFilter.SelectedValue is int selectedMode ? selectedMode : 0;
        var source = QueueSourceFilter.SelectedValue is int selectedSource && selectedSource >= 0 ? (LiveEventType?)selectedSource : null;
        QueueStateFilter.IsEnabled = mode != 1;
        if (mode == 1 && QueueStateFilter.SelectedIndex != 0) { QueueStateFilter.SelectedIndex = 0; }
        var state = QueueStateFilter.SelectedValue?.ToString() ?? ""; var search = QueueSearch.Text.Trim();
        QueueGrid.Visibility = mode == 0 ? Visibility.Visible : Visibility.Collapsed; LogGrid.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (mode == 0)
        {
            var selected = (QueueGrid.SelectedItem as InteractionJob)?.Id;
            var rows = runtime.Journal.QueryJobs(source, state, search); QueueGrid.ItemsSource = rows; QueueGrid.SelectedItem = rows.FirstOrDefault(row => row.Id == selected);
        }
        else if (mode == 1) { LogGrid.ItemsSource = runtime.Journal.QueryInputs(source, search); }
        else
        {
            LogGrid.ItemsSource = records.Where(row => (!source.HasValue || row.SourceType == source) && (state.Length == 0 || ((state is "Failed" or "Unknown") && !row.Success) || row.Kind == state) && (search.Length == 0 || (row.Message + row.Kind).Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        QueueOverviewText.Text = "本次实读 " + runtime.Received + " 条 · 待执行单位 " + runtime.Pending + " · 待解析帧 " + runtime.Journal.PendingFrames + " · 待核对输入 " + runtime.Journal.ReviewInputs + " · 待核对帧 " + runtime.Journal.ReviewFrames + "。当前显示最近 500 条匹配记录；完整输入与任务已保存。";
    }
    private void ResumeQueueClick(object? sender, RoutedEventArgs e) { runtime.ResumePending(); RefreshQueueView(); }
    private async void CheckUnknownClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (QueueGrid.SelectedItem is not InteractionJob job || job.Unknown == 0) { MessageBox.Show(this, "在执行队列中选择一条“结果待核对”的任务。", "核对回执"); return; }
        await runtime.CheckUnknownAsync(job.Id); RefreshQueueView();
    });
    private void RetryFramesClick(object? sender, RoutedEventArgs e)
    {
        var count = runtime.Journal.RetryFailedFrames(); runtime.Log("重解析", "已安排重新解析 " + count + " 帧，已入账事件不会重复触发。"); RefreshQueueView();
    }
    /// <summary>错误独立于日志刷新优先显示；合并高频错误，避免把 UI 事件队列塞满。</summary>
    private void ShowPendingError()
    {
        var critical = Interlocked.Exchange(ref pendingError, null); Interlocked.Exchange(ref errorDispatchQueued, 0);
        if (critical == null || closing) { return; }
        ConsoleErrorText.Text = critical.At.ToString("HH:mm:ss") + " · " + critical.Kind + " · " + critical.Message;
        ConsoleErrorBanner.Visibility = Visibility.Visible; FooterStatus.Foreground = Ui.Brush("#FFBEA8");
    }
    private void ClearErrorClick(object? sender, RoutedEventArgs e) { ConsoleErrorBanner.Visibility = Visibility.Collapsed; FooterStatus.Foreground = Ui.Brush("#A2B9C8"); }
    private void QueueDetailsClick(object? sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (QueueGrid.SelectedItem is not InteractionJob job) { return; }
        var content = new TextBox { Text = JsonConvert.SerializeObject(job, Formatting.Indented), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(18) };
        new Window { Title = job.Triggerer + " · " + job.Effect, Width = 760, Height = 650, Owner = this, Content = content, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }

    /// <summary>导出规则，不包含登录态、本机路径或永久许可。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void ExportRulesClick(object? sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        var dialog = new SaveFileDialog { Filter = "互动规则配置 (*.json)|*.json", FileName = "我的互动规则.json" }; // 配置分享位置。
        if (dialog.ShowDialog(this) == true) { ProfileStore.Save(dialog.FileName, profile); }
        return Task.CompletedTask;
    });

    /// <summary>自动发现环境，不向游戏目录写入。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void DetectGameClick(object? sender, RoutedEventArgs e) => RefreshEnvironment(true);

    /// <summary>选择游戏目录后重新检查。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void ChooseGameClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择含 Hollow Knight Silksong.exe 的游戏目录" }; // 用户明确指定维护目标。
        if (dialog.ShowDialog(this) == true) { GamePathBox.Text = dialog.FolderName; settings.GameDirectory = dialog.FolderName; RefreshEnvironment(false); SavePreferences(); }
    }

    /// <summary>一次点击完成备份与加载器、Mod 安装。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void PrepareGameClick(object? sender, RoutedEventArgs e) => await GuardAsync(() => MaintainAsync("install"));

    /// <summary>启用本工具 Mod。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void EnableModClick(object? sender, RoutedEventArgs e) => await GuardAsync(() => MaintainAsync("enable"));

    /// <summary>停用本工具 Mod，不停用其他插件。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void DisableModClick(object? sender, RoutedEventArgs e) => await GuardAsync(() => MaintainAsync("disable"));

    /// <summary>卸载本工具 Mod，保留其他插件和备份。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void UninstallModClick(object? sender, RoutedEventArgs e) => await GuardAsync(() => MaintainAsync("uninstall"));

    /// <summary>通过 Steam 正常启动正版游戏。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void LaunchGameClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () => { if (await EnsurePreparedAsync()) { LaunchGame(); } });

    /// <summary>打开已经选择的游戏目录。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void OpenGameFolderClick(object? sender, RoutedEventArgs e) { if (Directory.Exists(GamePathBox.Text)) { OpenPath(GamePathBox.Text); } }

    /// <summary>创建游戏关闭时的完整备份。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void BackupClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () => { var snapshot = await Task.Run(() => Backups().Create()); BackupStatusText.Text = "已备份：" + Path.GetFileName(snapshot); RefreshBackups(); });

    /// <summary>确认后恢复，并保留当前存档作为另一个恢复点。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void RestoreBackupClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (BackupList.SelectedItem is not string snapshot) { throw new InvalidOperationException("先选择一个备份。"); }
        if (MessageBox.Show(this, "正常退出游戏后恢复该备份。当前存档会另存为恢复点；Steam 云存档可能再次同步，请留意游戏提示。继续？", "恢复存档", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { return; }
        var recovery = await Task.Run(() => Backups().Restore(snapshot)); BackupStatusText.Text = "已恢复。恢复前的存档保存在：" + Path.GetFileName(recovery); RefreshBackups();
    });

    /// <summary>打开用户自己的备份位置。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void OpenBackupFolderClick(object? sender, RoutedEventArgs e) { Directory.CreateDirectory(Path.Combine(dataDirectory, "SaveBackups")); OpenPath(Path.Combine(dataDirectory, "SaveBackups")); }

    /// <summary>永久许可只在当前会话确认，不写入配置或导出文件。</summary>
    /// <param name="sender">许可开关。</param>
    /// <param name="e">事件。</param>
    private void PersistentChanged(object? sender, RoutedEventArgs e)
    {
        if (initializing || persistentPrompt) { return; }
        if (PersistentCheck.IsChecked != true) { runtime.AllowPersistent = false; return; }
        persistentPrompt = true;
        var approved = MessageBox.Show(this, "允许本次会话执行你配置的永久资源、装备和能力修改？这些可能随游戏自动存档。建议先正常退出并备份；紧急停止不能撤销永久修改。", "允许永久修改", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes; // 明确会话许可。
        runtime.AllowPersistent = approved; PersistentCheck.IsChecked = approved; persistentPrompt = false;
    }

    /// <summary>保存可选展示设置，并同步到连接中的游戏。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void SaveSettingsClick(object? sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        if (!int.TryParse(OverlayFontBox.Text, out var size) || size is < 12 or > 48) { throw new InvalidDataException("字号应为 12 至 48。"); }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1024 or > 65535) { throw new InvalidDataException("本机端口应为 1024 至 65535。"); }
        if (!int.TryParse(SpawnLimitBox.Text, out var limit) || limit is < 0 or > 10000) { throw new InvalidDataException("生成上限应为0至10000；0表示不设总上限。"); }
        if (!System.Text.RegularExpressions.Regex.IsMatch(AccentBox.Text, "^#[0-9a-fA-F]{6}$")) { throw new InvalidDataException("主色格式应为 #RRGGBB。"); }
        if (!int.TryParse(GameFontBox.Text, out var gameFont) || gameFont is < 12 or > 36) { throw new InvalidDataException("游戏内字号应为 12 至 36。"); }
        if (!double.TryParse(GameDurationBox.Text, CultureInfo.InvariantCulture, out var gameSeconds) || !double.IsFinite(gameSeconds) || gameSeconds is < 1 or > 60 || !double.TryParse(BrowserDurationBox.Text, CultureInfo.InvariantCulture, out var browserSeconds) || !double.IsFinite(browserSeconds) || browserSeconds is < 1 or > 60) { throw new InvalidDataException("提示持续时间应为 1 至 60 秒。"); }
        if (!System.Text.RegularExpressions.Regex.IsMatch(GameAccentBox.Text, "^#[0-9a-fA-F]{6}$")) { throw new InvalidDataException("游戏内主色格式应为 #RRGGBB。"); }
        settings.GameFontSize = gameFont; settings.GameCorner = GameCornerBox.SelectedValue?.ToString() ?? "BottomLeft"; settings.GameAccent = GameAccentBox.Text; settings.GameNoticeSeconds = gameSeconds; settings.BrowserNoticeSeconds = browserSeconds;
        settings.GameShowSource = GameSourceCheck.IsChecked == true; settings.GameShowResult = GameResultCheck.IsChecked == true; settings.GameShowEffects = GameEffectsCheck.IsChecked == true; settings.GameShowProgress = GameProgressCheck.IsChecked == true;
        settings.BrowserShowSource = BrowserSourceCheck.IsChecked == true; settings.BrowserShowResult = BrowserResultCheck.IsChecked == true; settings.BrowserShowEffects = BrowserEffectsCheck.IsChecked == true; settings.BrowserShowProgress = BrowserProgressCheck.IsChecked == true;
        var changedPort = port != settings.OverlayPort; // 服务端口的明确重启条件。
        settings.GameOverlay = GameOverlayCheck.IsChecked == true; settings.BrowserOverlay = BrowserOverlayCheck.IsChecked == true; settings.HideNicknames = HideNamesCheck.IsChecked == true;
        settings.FontSize = size; settings.OverlayPort = port; settings.Corner = CornerBox.SelectedValue?.ToString() ?? "BottomLeft"; settings.Accent = AccentBox.Text; settings.SpawnLimit = limit;
        runtime.HideNicknames = false; SavePreferences(); await ApplyGameSettingsAsync();
        SettingsResultText.Text = changedPort ? "设置已保存；本机展示端口将在重启工具后生效。" : "设置已应用。永久修改许可只对当前会话有效。";
    });

    /// <summary>复制只读浏览器源地址。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void CopyOverlayClick(object? sender, RoutedEventArgs e) { if (overlay.Url.Length > 0) { Clipboard.SetText(overlay.Url); SettingsResultText.Text = "已复制；在 OBS 的浏览器源中粘贴。"; } }

    /// <summary>显式生成可选展示预览，不计为真实直播或游戏执行。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void OverlayPreviewClick(object? sender, RoutedEventArgs e)
    {
        overlayNotice = new { At = DateTimeOffset.UtcNow, Title = "展示预览 · 你的自定义规则", Detail = "这是一条本地展示预览，不代表直播或游戏执行成功。", Success = true, Simulation = true };
        SettingsResultText.Text = "已发送本地展示预览，持续约六秒。"; PublishOverlay();
    }

    /// <summary>本人点击后打开本机只读展示页。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private void OpenOverlayClick(object? sender, RoutedEventArgs e) { if (overlay.Url.Length > 0) { OpenPath(overlay.Url); } }

    /// <summary>导出技术状态，不附带账号、观众消息或原始协议包。</summary>
    /// <param name="sender">按钮。</param>
    /// <param name="e">事件。</param>
    private async void ExportDiagnosticsClick(object? sender, RoutedEventArgs e) => await GuardAsync(() =>
    {
        var dialog = new SaveFileDialog { Filter = "脱敏诊断 (*.json)|*.json", FileName = "LiveInteract-诊断.json" }; // 用户选择导出文件。
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(new
            {
                Version = "0.7.1", At = DateTimeOffset.Now, Game = new { environment.GameFound, environment.LoaderFound, environment.ModEnabled, environment.ModDisabled, environment.Running, environment.BuildId },
                Bridge = new { runtime.Game.IsConnected, runtime.Game.Status?.GameVersion, runtime.Game.Status?.Scene, runtime.Game.Status?.CanAct, runtime.Game.Status?.Diagnostics },
                Counts = new { runtime.Received, runtime.Simulated, runtime.Completed, runtime.Failed },
                RuleCount = profile.Rules.Count, EnabledRules = profile.Rules.Count(item => item.Enabled),
                Checks = new { RealLive = "需要本人实际直播分别验证", GameEffects = "执行回执与实际战斗效果分开验收" }
            }, Formatting.Indented));
            FooterStatus.Text = "脱敏技术诊断已导出。";
        }
        return Task.CompletedTask;
    });

    /// <summary>从真实输入建立规则或复制已提供的 ID，无需手工找协议字段。</summary>
    /// <param name="sender">运行记录。</param>
    /// <param name="e">双击事件。</param>
    private void InputDetailsClick(object? sender, RoutedEventArgs e)
    {
        if ((LogGrid.SelectedItem as RuntimeEntry)?.SourceEvent is not { } input) { return; }
        var window = new Window { Title = "互动输入详情", Width = 530, Height = 520, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new(24) }; window.Content = new ScrollViewer { Content = panel };
        panel.Children.Add(new TextBlock { Text = input.Nickname + " · " + Ui.EventTypes.First(item => item.Value == input.Type).Name, FontSize = 22, FontWeight = FontWeights.SemiBold });
        var user = Ui.Field(panel, "用户 ID（空＝平台未提供）", input.UserId); user.IsReadOnly = true;
        var gift = Ui.Field(panel, "礼物 ID / 名称", input.GiftId + " / " + input.GiftName); gift.IsReadOnly = true;
        var text = Ui.Field(panel, "弹幕内容", input.Text); text.IsReadOnly = true; text.AcceptsReturn = true; text.Height = 70;
        panel.Children.Add(new TextBlock { Text = "新增数量 " + input.Count + " · 连击总量 " + input.ComboTotal + (input.IsSimulation ? " · 模拟输入" : " · 实际收到"), Foreground = Ui.Brush("#A8C2D0"), Margin = new(0, 15, 0, 15) });
        var row = new WrapPanel(); panel.Children.Add(row);
        var copyUser = new Button { Content = "复制用户 ID", IsEnabled = input.UserId.Length > 0, Margin = new(0, 0, 8, 10) }; copyUser.Click += (_, _) => Clipboard.SetText(input.UserId); row.Children.Add(copyUser);
        var copyGift = new Button { Content = "复制礼物 ID", IsEnabled = input.GiftId.Length > 0, Margin = new(0, 0, 8, 10) }; copyGift.Click += (_, _) => Clipboard.SetText(input.GiftId); row.Children.Add(copyGift);
        var create = new Button { Content = "用这条输入创建规则", Style = (Style)FindResource("PrimaryButton"), Margin = new(0, 0, 0, 10) };
        create.Click += (_, _) => { window.Close(); EditRule(new() { Name = input.Type == LiveEventType.Chat ? "弹幕 · " + input.Text[..Math.Min(input.Text.Length, 30)] : Ui.EventTypes.First(item => item.Value == input.Type).Name + " · " + input.GiftName, Trigger = new() { Type = input.Type, TextMatching = TextMatching.Exact, Keywords = input.Type == LiveEventType.Chat ? [input.Text] : [], GiftId = input.GiftId, GiftName = input.GiftName } }, true); };
        panel.Children.Add(create); window.ShowDialog();
    }

    /// <summary>维护操作的完整用户路径：确认目标、备份、自动安装或安排退出后执行。</summary>
    /// <param name="operation">安装、启停或卸载。</param>
    /// <returns>维护任务。</returns>
    private async Task MaintainAsync(string operation)
    {
        if (maintenanceBusy) { return; }
        RefreshEnvironment(false);
        if (!environment.GameFound || environment.Problem.Length > 0) { Navigation.SelectedIndex = 4; throw new InvalidOperationException(environment.Problem.Length > 0 ? environment.Problem : "先选择正确的 Steam 游戏目录。"); }
        var description = operation switch { "install" => "自动备份存档，安装或修复加载器及本工具 Mod", "enable" => "启用本工具 Mod", "disable" => "停用本工具 Mod", "uninstall" => "卸载本工具 Mod，保留其他插件与备份", _ => throw new InvalidOperationException("未知维护操作。") }; // 确认摘要。
        if (MessageBox.Show(this, description + "？\n\n目标：" + environment.Directory + (environment.Running ? "\n\n游戏正在运行：会立即停止互动，等你正常退出后自动执行，不会强制关闭。" : ""), "确认游戏维护", MessageBoxButton.YesNo, operation == "uninstall" ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) { return; }
        settings.GameDirectory = environment.Directory; settings.ApprovedGameDirectory = environment.Directory;
        await runtime.EmergencyStopAsync();
        if (environment.Running) { settings.PendingMaintenance = operation; SavePreferences(); MaintenanceStatusText.Text = "已安排：" + description + "。等待你正常退出游戏。"; FooterStatus.Text = MaintenanceStatusText.Text; return; }
        await ExecuteMaintenanceAsync(operation);
    }

    /// <summary>在已经确认的目录进行维护；需要权限时仅提升这个短时维护进程。</summary>
    /// <param name="operation">维护操作。</param>
    /// <returns>维护任务。</returns>
    private async Task ExecuteMaintenanceAsync(string operation)
    {
        maintenanceBusy = true; PrepareButton.IsEnabled = false; MaintenanceStatusText.Text = "正在" + (operation == "install" ? "备份与准备游戏…" : "维护 Mod…");
        try
        {
            var directory = environment.Directory; // 已确认且当前仍正确的目录。
            string result;
            try { result = await Task.Run(() => Maintenance.Run(operation, directory, App.PayloadDirectory, dataDirectory)); }
            catch (UnauthorizedAccessException) { result = await Maintenance.ElevateAsync(operation, directory, dataDirectory); }
            MaintenanceStatusText.Text = result; FooterStatus.Text = result; runtime.Log("维护", result); settings.PendingMaintenance = ""; SavePreferences(); RefreshEnvironment(false); RefreshBackups();
        }
        finally { maintenanceBusy = false; PrepareButton.IsEnabled = true; }
    }

    /// <summary>执行前自动检查并复用已经确认的安装许可。</summary>
    /// <returns>Mod 已准备时返回真。</returns>
    private async Task<bool> EnsurePreparedAsync()
    {
        RefreshEnvironment(false);
        if (environment.ModEnabled && environment.LoaderFound && !ModNeedsUpdate()) { return true; }
        if (environment.GameFound && !environment.Running && string.Equals(settings.ApprovedGameDirectory, environment.Directory, StringComparison.OrdinalIgnoreCase))
        {
            await ExecuteMaintenanceAsync("install");
        }
        else { await MaintainAsync(environment.ModDisabled ? "enable" : "install"); }
        RefreshEnvironment(false); return environment.ModEnabled && environment.LoaderFound;
    }

    /// <summary>比较本工具明确版本号，发现新组件时自动进入已确认的准备流程。</summary>
    /// <returns>安装组件与当前便携包不同则返回真。</returns>
    private bool ModNeedsUpdate()
    {
        foreach (var name in new[] { "LiveInteract.Silksong.dll", "LiveInteract.Contracts.dll" })
        {
            var expected = Path.Combine(App.PayloadDirectory, "mod", name); // 发行包的已知组件。
            var installed = Path.Combine(environment.Directory, GameDetector.EnabledPath, name);
            if (File.Exists(expected) && (!File.Exists(installed) || FileVersionInfo.GetVersionInfo(expected).FileVersion != FileVersionInfo.GetVersionInfo(installed).FileVersion)) { return true; }
        }
        return false;
    }

    /// <summary>读取而不改变游戏环境，主动检测时可以自动发现安装位置。</summary>
    /// <param name="autoDetect">是否扫描已登记 Steam 库。</param>
    private void RefreshEnvironment(bool autoDetect)
    {
        try
        {
            environment = autoDetect || string.IsNullOrWhiteSpace(GamePathBox.Text) ? GameDetector.Detect() : GameDetector.Inspect(GamePathBox.Text.Trim());
            if (environment.GameFound) { GamePathBox.Text = environment.Directory; settings.GameDirectory = environment.Directory; }
            var mod = environment.ModEnabled ? (ModNeedsUpdate() ? "检测到专用 Mod 更新" : "专用 Mod 已启用") : environment.ModDisabled ? "专用 Mod 已停用" : "尚未安装专用 Mod"; // 不把文件存在当成游戏运行验证。
            PrepareSummary.Text = environment.GameFound ? "已找到 Steam 游戏 · " + mod + "。" : environment.Problem;
            PrepareButton.Content = environment.ModEnabled ? "检查 / 修复" : environment.Running ? "退出后自动准备" : "一键准备游戏";
            GameDetailsText.Text = environment.GameFound ? "Steam 游戏：已检测\n游戏 Build：" + environment.BuildId + "（此包编译目标 22479045；实际效果待逐项验证）\n加载器：" + (environment.LoaderFound ? "已检测" : "未安装") + "　/　" + mod + "\n进程：" + (environment.Running ? "正在运行" : "未运行") + (environment.Problem.Length > 0 ? "\n" + environment.Problem : "") : environment.Problem;
        }
        catch (Exception exception) { FooterStatus.Text = "检测未完成：" + exception.Message; }
    }

    /// <summary>定期显示实际状态、处理退出后维护和广播只读展示。</summary>
    /// <returns>界面更新任务。</returns>
    private async Task RefreshUiAsync()
    {
        ShowPendingError();
        if (ticks % 2 == 0 && QueueAutoRefresh.IsChecked == true) { RefreshQueueView(); }
        for (var index = 0; index < 80 && inbox.TryDequeue(out var entry); index++) { records.Insert(0, entry); if (records.Count > 1200) { records.RemoveAt(records.Count - 1); } FooterStatus.Text = entry.Message; }
        SuccessCount.Text = runtime.Completed.ToString(); InputCount.Text = "真实事件 " + runtime.Received + "   ·   模拟 " + runtime.Simulated; FooterQueue.Text = "队列 " + runtime.Pending + "  ·  未执行 " + runtime.Failed;
        LiveButton.Content = runtime.Enabled ? "暂停新触发" : "开启互动"; GameDot.Fill = Ui.Brush(runtime.Game.IsConnected ? "#79E0C8" : "#D4A965");
        GameStatusText.Text = runtime.Game.IsConnected ? (runtime.Game.Status?.CanAct == true ? "游戏已连接 · 可操作" : "游戏已连接 · 等待进入场景") : environment.GameFound ? "游戏未连接 · " + (environment.ModEnabled ? "请启动游戏" : "先准备 Mod") : "未找到游戏";
        if (++ticks % 8 == 0)
        {
            RefreshEnvironment(false); PublishOverlay();
            if (runtime.Game.IsConnected) { await ApplyGameSettingsAsync(); }
            if (!preview && !maintenanceBusy && settings.PendingMaintenance.Length > 0 && !GameDetector.IsRunning() && string.Equals(settings.ApprovedGameDirectory, environment.Directory, StringComparison.OrdinalIgnoreCase))
            {
                try { await ExecuteMaintenanceAsync(settings.PendingMaintenance); }
                catch (Exception exception) { settings.PendingMaintenance = ""; SavePreferences(); runtime.Log("维护失败", exception.Message, false); MaintenanceStatusText.Text = exception.Message; }
            }
        }
    }

    /// <summary>把可选提示设置发送到专用 Mod。</summary>
    /// <returns>同步任务。</returns>
    private async Task ApplyGameSettingsAsync()
    {
        if (!runtime.Game.IsConnected) { return; }
        try { await runtime.Game.ConfigureAsync(JsonConvert.SerializeObject(new { enabled = settings.GameOverlay, fontSize = settings.GameFontSize, corner = settings.GameCorner, accent = settings.GameAccent, seconds = settings.GameNoticeSeconds, showSource = settings.GameShowSource, showResult = settings.GameShowResult, showEffects = settings.GameShowEffects, showProgress = settings.GameShowProgress, progress = profile.Rules.Where(item => item.Enabled && item.Trigger.Counting == TriggerCounting.RoomTotal).Take(4).Select(item => item.Name + " · " + runtime.RuleProgress(item.Id).Accumulated + "/" + item.Trigger.Threshold).ToArray(), spawnLimit = settings.SpawnLimit })); }
        catch (IOException exception) { runtime.Log("设置同步", exception.Message, false); }
    }

    /// <summary>广播不含登录态的显示内容。</summary>
    private void PublishOverlay()
    {
        overlay.Publish(new { Enabled = settings.BrowserOverlay, FontSize = settings.FontSize, Corner = settings.Corner, Accent = settings.Accent, NoticeSeconds = settings.BrowserNoticeSeconds, Notice = overlayNotice, Effects = settings.BrowserShowEffects ? runtime.Game.Status?.ActiveEffects ?? [] : [], Progress = profile.Rules.Where(item => settings.BrowserShowProgress && item.Enabled && item.Trigger.Counting == TriggerCounting.RoomTotal).Take(4).Select(item => new { item.Name, Current = runtime.RuleProgress(item.Id).Accumulated, item.Trigger.Threshold }).ToArray() });
    }

    /// <summary>读取当前选中规则。</summary>
    /// <returns>规则或空。</returns>
    private RuleDefinition? SelectedRule() => (RulesGrid.SelectedItem as RuleRow)?.Rule;

    /// <summary>显示规则编辑器并保存完整编辑结果。</summary>
    /// <param name="rule">规则源。</param>
    /// <param name="adding">是否新建。</param>
    private void EditRule(RuleDefinition rule, bool adding)
    {
        var editor = new RuleEditorWindow(rule, runtime.Game.Status) { Owner = this }; // 可取消的编辑副本。
        if (editor.ShowDialog() != true || editor.Result == null) { return; }
        if (adding || editor.CreatesNewRule) { profile.Rules.Add(editor.Result); }
        else { profile.Rules[profile.Rules.IndexOf(rule)] = editor.Result; }
        SaveRules(); Navigation.SelectedIndex = 1;
    }

    /// <summary>调整规则顺序并立即应用。</summary>
    /// <param name="offset">移动方向。</param>
    private void MoveRule(int offset)
    {
        if (SelectedRule() is not { } rule) { return; }
        var index = profile.Rules.IndexOf(rule); var target = index + offset; // 有效目标位置。
        if (target < 0 || target >= profile.Rules.Count) { return; }
        profile.Rules.RemoveAt(index); profile.Rules.Insert(target, rule); SaveRules();
    }

    /// <summary>保存已验证规则；错误保留在界面而不静默失败。</summary>
    private void SaveRules()
    {
        try { ProfileStore.Save(profileFile, profile); runtime.ApplyProfile(profile); RefreshRules(); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "规则尚未保存", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>重建规则列表和空状态说明。</summary>
    private void RefreshRules()
    {
        var selected = SelectedRule()?.Id; // 保留用户正在操作的规则。
        var rows = profile.Rules.Select(item => new RuleRow(item)).ToList(); RulesGrid.ItemsSource = rows; RulesGrid.SelectedItem = rows.FirstOrDefault(item => item.Rule.Id == selected); RuleSummary.Text = profile.Rules.Count == 0 ? "还没有规则。创建一条，决定什么互动触发什么功能。" : "已有 " + profile.Rules.Count + " 条规则，启用 " + profile.Rules.Count(item => item.Enabled) + " 条。";
        RulesHint.Text = profile.Rules.Count == 0 ? "尚未创建规则。点击“新建规则”，选一个触发、一个效果，填好数量即可。" : "按列表顺序匹配；导入的规则默认关闭，总开关单独控制。";
    }

    /// <summary>创建固定于当前用户的存档备份服务。</summary>
    /// <returns>备份服务。</returns>
    private SaveBackups Backups() => new(Maintenance.SaveDirectory, Path.Combine(dataDirectory, "SaveBackups"));

    /// <summary>显示本工具持有的备份列表。</summary>
    private void RefreshBackups()
    {
        BackupList.ItemsSource = Backups().List(); if (BackupList.Items.Count > 0) { BackupList.SelectedIndex = 0; }
        BackupStatusText.Text = BackupList.Items.Count == 0 ? "尚无备份。首次准备游戏时会自动创建一个快照。" : "已有 " + BackupList.Items.Count + " 个可恢复快照。";
    }

    /// <summary>保存可恢复的本机设置。</summary>
    private void SavePreferences()
    {
        try { settings.Save(settingsFile); }
        catch (Exception exception) { runtime.Log("设置保存", exception.Message, false); }
    }

    /// <summary>正常通过 Steam 启动，不调用桌面控制。</summary>
    private static void LaunchGame() { if (!GameDetector.IsRunning()) { OpenPath("steam://rungameid/1030300"); } }

    /// <summary>仅响应用户点击打开指定本地位置或浏览器页面。</summary>
    /// <param name="value">明确位置。</param>
    private static void OpenPath(string value) => Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });

    /// <summary>把操作失败转换为可以处理的界面提示。</summary>
    /// <param name="action">用户操作。</param>
    /// <returns>操作任务。</returns>
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { FooterStatus.Text = "操作已取消。"; }
        catch (Exception exception) { runtime.Log("操作未完成", exception.Message, false); FooterStatus.Text = exception.Message; MessageBox.Show(this, exception.Message, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    /// <summary>注册两项全局保护快捷键，冲突时明确报告。</summary>
    private void RegisterKeys()
    {
        var handle = new WindowInteropHelper(this).Handle; // 当前工具窗口。
        hotkeySource = HwndSource.FromHwnd(handle); hotkeySource?.AddHook(HotkeyMessage);
        var pause = HotkeyWindow.Parse(settings.PauseHotkey); var emergency = HotkeyWindow.Parse(settings.EmergencyHotkey); // 本人已保存的组合键。
        if (!RegisterHotKey(handle, 10, pause.Modifiers, pause.Key)) { runtime.Log("快捷键", "Ctrl+Alt+F10 注册失败，可能被占用；界面暂停按钮仍可使用。", false); }
        if (!RegisterHotKey(handle, 12, emergency.Modifiers, emergency.Key)) { runtime.Log("快捷键", "Ctrl+Alt+F12 注册失败，可能被占用；界面紧急停止仍可使用。", false); }
    }

    /// <summary>本人设置组合键并立即检查占用，失败时恢复旧绑定。</summary>
    /// <param name="sender">更改按钮。</param>
    /// <param name="e">事件。</param>
    private void ChangeHotkeyClick(object? sender, RoutedEventArgs e)
    {
        var emergency = (sender as Button)?.Tag?.ToString() == "emergency"; // 要更改的保护操作。
        var current = emergency ? settings.EmergencyHotkey : settings.PauseHotkey;
        var dialog = new HotkeyWindow(current) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedHotkey == null) { return; }
        if (dialog.SelectedHotkey == (emergency ? settings.PauseHotkey : settings.EmergencyHotkey)) { MessageBox.Show(this, "两项操作不能使用同一快捷键。", "快捷键冲突"); return; }
        var id = emergency ? 12 : 10; var handle = new WindowInteropHelper(this).Handle; var next = HotkeyWindow.Parse(dialog.SelectedHotkey); // 当前窗口与新绑定。
        if (!preview)
        {
            UnregisterHotKey(handle, id);
            if (!RegisterHotKey(handle, id, next.Modifiers, next.Key))
            {
                var old = HotkeyWindow.Parse(current); RegisterHotKey(handle, id, old.Modifiers, old.Key); MessageBox.Show(this, "该组合键已被占用，已保留原来的快捷键。", "无法注册快捷键"); return;
            }
        }
        if (emergency) { settings.EmergencyHotkey = dialog.SelectedHotkey; } else { settings.PauseHotkey = dialog.SelectedHotkey; }
        PauseHotkeyText.Text = settings.PauseHotkey + " · 暂停 / 恢复新触发"; EmergencyHotkeyText.Text = settings.EmergencyHotkey + " · 紧急停止与恢复"; SavePreferences();
    }

    /// <summary>只处理本工具注册的保护快捷键。</summary>
    /// <param name="hwnd">窗口。</param>
    /// <param name="message">消息。</param>
    /// <param name="wParam">快捷键 ID。</param>
    /// <param name="lParam">附加信息。</param>
    /// <param name="handled">是否处理。</param>
    /// <returns>默认消息结果。</returns>
    private nint HotkeyMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312) { handled = true; if (wParam == 12) { _ = runtime.EmergencyStopAsync(); } else if (wParam == 10) { if (runtime.Enabled) { runtime.SetEnabled(false); } else { ToggleLive(this, new()); } } }
        return 0;
    }

    /// <summary>等待临时效果恢复和服务停止后关闭，避免先关闭进程再丢失恢复命令。</summary>
    /// <param name="sender">窗口。</param>
    /// <param name="e">关闭事件。</param>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) { return; }
        e.Cancel = true; closing = true; timer.Stop(); source?.Close();
        if (!preview) { var handle = new WindowInteropHelper(this).Handle; UnregisterHotKey(handle, 10); UnregisterHotKey(handle, 12); }
        SavePreferences();
        try { await runtime.DisposeAsync(); await overlay.DisposeAsync(); }
        catch (Exception exception) { File.WriteAllText(Path.Combine(dataDirectory, "shutdown-error.txt"), exception.ToString()); }
        _ = Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }

    /// <summary>生成真实 WPF 布局截图和编辑器截图，用于界面验收而非系统桌面控制。</summary>
    /// <param name="folder">验收图像目录。</param>
    /// <returns>生成任务。</returns>
    public async Task RenderPreviewsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        if (preview)
        {
            foreach (var example in new[] { ("演示小明", LiveEventType.Gift, "spawn.shellwood-wasp", "count", "3"), ("演示小红", LiveEventType.Chat, "boss.heal", "value", "100"), ("演示小林", LiveEventType.Like, "health.add", "value", "2") })
            {
                var input = new LiveEvent { Id = Guid.NewGuid().ToString("N"), RoomId = "preview-only", Nickname = example.Item1, Type = example.Item2, Text = "界面演示", GiftName = "小心心", IsSimulation = true };
                runtime.Journal.AdmitEvents([input], true);
                runtime.Journal.CommitDecision(input, [new RuleMatch(new RuleDefinition { Name = "界面演示", Root = new() { ActionId = example.Item3, Arguments = new() { [example.Item4] = example.Item5 } } }, 1)], "");
            }
            var rows = runtime.Journal.QueryJobs();
            runtime.Journal.Defer(rows.First(row => row.SourceType == LiveEventType.Chat).Id, "演示：等待 Boss 出现，未消耗单位");
            runtime.Journal.CompleteUnit(rows.First(row => row.SourceType == LiveEventType.Like).Id, new() { Success = true, Message = "界面演示记录，不代表本次游戏生效" });
            RefreshQueueView();
        }
        foreach (var page in new[] { 0, 1, 2, 3, 4, 5 })
        {
            Navigation.SelectedIndex = page; if (page == 2 && ActionsList.Items.Count > 0) { ActionsList.SelectedIndex = 0; }
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); await Task.Delay(120); UpdateLayout();
            Capture(this, Path.Combine(folder, "desktop-" + page + ".png"));
        }
        foreach (var example in new[]
        {
            ("rule-editor", "spawn.bone-goomba", "count", "3"),
            ("rule-healing", "health.add", "value", "4"),
            ("rule-boss-heal", "boss.heal", "value", "300")
        })
        {
            var rule = new RuleDefinition { Name = "", Enabled = true, Trigger = new() { Type = LiveEventType.Gift, GiftName = "小心心" }, Root = new() { ActionId = example.Item2, Arguments = new() { [example.Item3] = example.Item4 } } };
            var editor = new RuleEditorWindow(rule, null) { Owner = this };
            editor.Show(); await Task.Delay(180); editor.UpdateLayout(); Capture(editor, Path.Combine(folder, example.Item1 + ".png")); editor.Close();
        }
        Navigation.SelectedIndex = 0;
        File.WriteAllText(Path.Combine(folder, "desktop-smoke.json"), JsonConvert.SerializeObject(new { Started = true, PagesRendered = 6, RuleEditorRendered = true, ActionDefinitions = ActionCatalog.All.Count, Game = new { environment.GameFound, environment.BuildId }, NoGameFilesChanged = true }, Formatting.Indented));
    }

    /// <summary>捕获当前应用自身可视树，不读取其他窗口。</summary>
    /// <param name="window">本工具窗口。</param>
    /// <param name="file">输出 PNG。</param>
    private static void Capture(Window window, string file)
    {
        var content = (FrameworkElement)window.Content; // 客户区包含根布局外边距，不捕获系统标题栏。
        var image = new RenderTargetBitmap((int)(content.ActualWidth + content.Margin.Left + content.Margin.Right), (int)(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, System.Windows.Media.PixelFormats.Pbgra32); // 当前窗口像素。
        image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var output = File.Create(file); encoder.Save(output);
    }

    /// <summary>Windows 注册快捷键入口。</summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="id">本工具标识。</param>
    /// <param name="modifiers">组合修饰键。</param>
    /// <param name="key">按键。</param>
    /// <returns>注册成功返回真。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    /// <summary>Windows 取消快捷键入口。</summary>
    /// <param name="window">目标窗口。</param>
    /// <param name="id">本工具标识。</param>
    /// <returns>取消成功返回真。</returns>
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    /// <summary>规则列表只读展示行。</summary>
    private sealed class RuleRow
    {
        /// <summary>对应规则。</summary>
        public RuleDefinition Rule { get; }
        /// <summary>中文启停状态。</summary>
        public string State => Rule.Enabled ? "已启用" : "已停用";
        /// <summary>显示名称。</summary>
        public string Name => Rule.Name;
        /// <summary>简明触发说明。</summary>
        public string Trigger => RuleDisplay.Trigger(Rule.Trigger);
        /// <summary>动作摘要。</summary>
        public string Actions => RuleDisplay.Effect(Rule.Root);
        /// <summary>冷却摘要。</summary>
        public string Cooldown => Rule.CooldownSeconds > 0 ? Rule.CooldownSeconds + " 秒" : "不限";

        /// <summary>包装规则而不修改原对象。</summary>
        /// <param name="rule">规则。</param>
        public RuleRow(RuleDefinition rule) => Rule = rule;
    }
}
