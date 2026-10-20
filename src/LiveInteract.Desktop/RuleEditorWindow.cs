using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LiveInteract.Contracts;
using LiveInteract.Core;
using Newtonsoft.Json;

namespace LiveInteract.Desktop;

/// <summary>一个触发对应一个可配置效果；旧组合只能明确另存，不截断原配置。</summary>
internal sealed class RuleEditorWindow : Window
{
    // 全部改动只落到编辑副本；取消不写回。
    private readonly RuleDefinition rule;
    private readonly GameStatus? game;
    private readonly StackPanel fields = new();
    private readonly TextBlock error = new() { Foreground = Ui.Brush("#F4B38F"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock sourceHint = new() { Foreground = Ui.Brush("#91AEBE"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 14) };
    private readonly TextBlock summary = new() { Foreground = Ui.Brush("#9AEDD7"), FontSize = 18, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock effectTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new(0, 17, 0, 2) };
    private readonly TextBlock effectDescription = new() { Foreground = Ui.Brush("#A5BDCB"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
    private readonly TextBox name, keywords, giftId, giftName, giftMinimum, giftMaximum, threshold, cooldown, userCooldown, perEvent;
    private readonly TextBox effectSearch = new() { ToolTip = "搜索怪物、回血、Boss、丝技……" };
    private readonly ComboBox type, category;
    private readonly CheckBox enabled;
    private readonly ListBox effectChoice = new() { DisplayMemberPath = "Name", SelectedValuePath = "Id", Height = 180 };
    private bool rebuilding;

    /// <summary>保存后的单效果规则。</summary>
    public RuleDefinition? Result { get; private set; }
    /// <summary>旧组合或数量绑定需要另存，原规则不修改。</summary>
    public bool CreatesNewRule { get; }

    /// <summary>编辑一个触发和一个效果。</summary>
    /// <param name="source">原规则。</param>
    /// <param name="status">实际游戏可用对象。</param>
    public RuleEditorWindow(RuleDefinition source, GameStatus? status)
    {
        rule = JsonConvert.DeserializeObject<RuleDefinition>(JsonConvert.SerializeObject(source))!;
        game = status;
        CreatesNewRule = rule.Root.Kind != StepKind.Action || rule.Root.Children.Count > 0 || rule.Root.ParameterBindings.Count > 0 || rule.Root.DelaySeconds > 0;
        if (CreatesNewRule)
        {
            rule.Id = Guid.NewGuid().ToString("N"); rule.Name += " · 单效果"; rule.Enabled = false;
            rule.Root = new() { ActionId = "spawn.bone-goomba" };
        }
        Title = "设置互动效果 · LiveInteract 0.4";
        Width = Math.Min(1160, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(850, SystemParameters.WorkArea.Height - 40);
        MinWidth = 940; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid { Margin = new(26) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); Content = root;
        var header = new StackPanel { Margin = new(0, 0, 0, 20) };
        header.Children.Add(new TextBlock { Text = "一个互动，一个效果。", FontSize = 26, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = CreatesNewRule ? "旧组合保持不变。这次会另存一条单效果规则，不覆盖原规则。" : "选一个触发，再选一个效果。数量、回血量、持续时间都由你填写。", Foreground = Ui.Brush("#A5BDCB"), Margin = new(0, 8, 0, 0) });
        root.Children.Add(header);
        var body = new Grid(); body.ColumnDefinitions.Add(new() { Width = new(310) }); body.ColumnDefinitions.Add(new() { Width = new(30) }); body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        Grid.SetRow(body, 1); root.Children.Add(body);
        var left = new StackPanel { Margin = new(0, 0, 12, 0) };
        body.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        left.Children.Add(Heading("01", "什么时候触发"));
        type = Ui.Choice(left, "互动来源", Ui.EventTypes, rule.Trigger.Type);
        var chat = new StackPanel(); left.Children.Add(chat);
        keywords = Ui.Field(chat, "弹幕关键词（每行一个，可留空）", string.Join("\n", rule.Trigger.Keywords));
        keywords.AcceptsReturn = true; keywords.MinHeight = 60;
        var gifts = new StackPanel(); left.Children.Add(gifts);
        giftName = Ui.Field(gifts, "礼物名称", rule.Trigger.GiftName);
        gifts.Children.Add(new TextBlock { Text = "例如：小心心。也可以从运行记录直接选择收到的礼物。", Foreground = Ui.Brush("#91AEBE"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 7, 0, 0) });
        giftMinimum = Ui.Field(gifts, "礼物单价下限（钻，0 不限）", rule.Trigger.MinGiftUnitValue.ToString(CultureInfo.InvariantCulture));
        giftMaximum = Ui.Field(gifts, "礼物单价上限（钻，0 不限）", rule.Trigger.MaxGiftUnitValue.ToString(CultureInfo.InvariantCulture));
        threshold = Ui.Field(left, "每多少个 / 次触发一次", rule.Trigger.Threshold.ToString(CultureInfo.InvariantCulture));
        left.Children.Add(sourceHint);
        enabled = new CheckBox { Content = "启用这条规则", IsChecked = rule.Enabled, Margin = new(0, 5, 0, 14) }; left.Children.Add(enabled);
        var options = new StackPanel { Margin = new(0, 4, 0, 14) };
        name = Ui.Field(options, "规则名称（留空自动命名）", rule.Name is "我的新规则" or "" ? "" : rule.Name);
        cooldown = Ui.Field(options, "整条规则执行间隔（秒，0 不限制）", rule.CooldownSeconds.ToString(CultureInfo.InvariantCulture));
        userCooldown = Ui.Field(options, "同一观众执行间隔（秒，0 不限制）", rule.UserCooldownSeconds.ToString(CultureInfo.InvariantCulture));
        perEvent = new TextBox { Text = rule.MaximumPerEvent.ToString(CultureInfo.InvariantCulture) };
        giftId = Ui.Field(options, "礼物 ID（可留空，填写后优先匹配）", rule.Trigger.GiftId);
        if (rule.Trigger.AllowedUsers.Count > 0 || rule.Trigger.BlockedUsers.Count > 0 || rule.Trigger.MinimumFanLevel > 0 || rule.Trigger.RequireFollower || rule.Probability < 1 || rule.Trigger.SingleExecution)
        {
            options.Children.Add(new TextBlock { Text = "这条旧规则还包含身份、概率或计数限制，将保留原设置。", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Brush("#F4B38F"), Margin = new(0, 12, 0, 0) });
        }
        left.Children.Add(new Expander { Header = "防刷屏与其他设置（可选）", Content = options, Foreground = Ui.Brush("#B5CBD7"), Margin = new(0, 4, 0, 0) });
        var right = new StackPanel(); Grid.SetColumn(right, 2);
        var rightScroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(rightScroll, 2); body.Children.Add(rightScroll);
        right.Children.Add(Heading("02", "触发什么效果"));
        right.Children.Add(Ui.Label("按分类选择，或输入怪物 / 效果名称搜索"));
        var filters = new Grid { Margin = new(0, 0, 0, 8) }; filters.ColumnDefinitions.Add(new() { Width = new(150) }); filters.ColumnDefinitions.Add(new() { Width = new(12) }); filters.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        category = Ui.Choice<string>(null, "", new[] { ("全部效果", "全部效果") }.Concat(ActionCatalog.UserFacing.Select(a => a.Category).Distinct().Select(c => (c, c))), "全部效果");
        filters.Children.Add(category); Grid.SetColumn(effectSearch, 2); filters.Children.Add(effectSearch); right.Children.Add(filters);
        right.Children.Add(effectChoice); right.Children.Add(effectTitle); right.Children.Add(effectDescription); right.Children.Add(fields);
        category.SelectionChanged += (_, _) => FilterEffects(); effectSearch.TextChanged += (_, _) => FilterEffects();
        effectChoice.SelectionChanged += (_, _) => SelectEffect();
        fields.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdatePreview()));
        fields.AddHandler(System.Windows.Controls.Primitives.Selector.SelectionChangedEvent, new SelectionChangedEventHandler((_, _) => UpdatePreview()));
        void UpdateSource(bool changed)
        {
            chat.Visibility = (LiveEventType)type.SelectedValue == LiveEventType.Chat ? Visibility.Visible : Visibility.Collapsed;
            gifts.Visibility = (LiveEventType)type.SelectedValue == LiveEventType.Gift ? Visibility.Visible : Visibility.Collapsed;
            if (changed)
            {
                rule.Trigger.Counting = (LiveEventType)type.SelectedValue is LiveEventType.Gift ? TriggerCounting.PerEvent : TriggerCounting.RoomTotal;
                if ((LiveEventType)type.SelectedValue == LiveEventType.Like && threshold.Text == "1") { threshold.Text = "100"; }
            }
            sourceHint.Text = (LiveEventType)type.SelectedValue switch
            {
                LiveEventType.Gift => "每份礼物独立触发，连送按实际新增数量计算。",
                LiveEventType.Like => "所有观众的点赞一起累计，达到门槛触发一次。",
                LiveEventType.Chat => "匹配任一行关键词。留空会匹配全部弹幕。",
                _ => "按收到的互动次数累计，达到门槛触发一次。"
            };
            UpdatePreview();
        }
        type.SelectionChanged += (_, _) => UpdateSource(true);
        giftName.TextChanged += (_, _) => { giftId.Text = ""; UpdatePreview(); };
        foreach (var box in new[] { keywords, threshold, giftMinimum, giftMaximum }) { box.TextChanged += (_, _) => UpdatePreview(); }
        var bottom = new Grid { Margin = new(0, 20, 0, 0) }; bottom.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); bottom.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var info = new StackPanel { Margin = new(0, 0, 18, 0) }; info.Children.Add(summary); info.Children.Add(error); bottom.Children.Add(info);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var cancel = new Button { Content = "取消", Margin = new(0, 0, 10, 0), IsCancel = true }; cancel.Click += (_, _) => Close();
        var save = new Button { Content = CreatesNewRule ? "另存为单效果规则" : "保存规则", Style = (Style)FindResource("PrimaryButton") }; save.Click += (_, _) => SaveRule();
        buttons.Children.Add(cancel); buttons.Children.Add(save); Grid.SetColumn(buttons, 1); bottom.Children.Add(buttons); Grid.SetRow(bottom, 2); root.Children.Add(bottom);
        FilterEffects(); UpdateSource(false);
    }

    /// <summary>筛选效果，搜索不会改写当前选择或参数。</summary>
    private void FilterEffects()
    {
        rebuilding = true;
        var query = effectSearch.Text.Trim(); var group = category.SelectedValue?.ToString() ?? "全部效果";
        var available = ActionCatalog.UserFacing.ToList();
        if (!available.Any(a => a.Id == rule.Root.ActionId)) { available.Add(ActionCatalog.All.First(a => a.Id == rule.Root.ActionId)); }
        effectChoice.ItemsSource = available.Where(a => (group == "全部效果" || a.Category == group) && (query.Length == 0 || (a.Name + a.Category).Contains(query, StringComparison.OrdinalIgnoreCase))).OrderBy(a => a.Category == "生成怪物" ? 0 : a.Category == "生命" ? 1 : a.Category == "丝线" ? 2 : a.Category == "丝技" ? 3 : 4).ThenBy(a => a.Category).ThenBy(a => a.Name).ToList();
        effectChoice.SelectedValue = rule.Root.ActionId;
        if (effectChoice.SelectedItem != null) { effectChoice.ScrollIntoView(effectChoice.SelectedItem); }
        rebuilding = false;
        if (fields.Children.Count == 0) { ShowEffect(); }
    }

    /// <summary>一个效果取代当前编辑副本，不创建动作链。</summary>
    private void SelectEffect()
    {
        if (rebuilding || effectChoice.SelectedItem is not ActionDescriptor selected) { return; }
        if (rule.Root.ActionId != selected.Id) { rule.Root = new() { ActionId = selected.Id }; }
        ShowEffect(); UpdatePreview();
    }

    /// <summary>只展示当前效果的参数。</summary>
    private void ShowEffect()
    {
        var descriptor = ActionCatalog.All.First(a => a.Id == rule.Root.ActionId);
        effectTitle.Text = descriptor.Name;
        effectDescription.Text = descriptor.Description + (descriptor.IsPersistent ? " 永久修改：执行前需要本次会话许可。" : "");
        Ui.ActionFields(fields, rule.Root, game);
    }

    /// <summary>实时预览真实参数，不保存、不调用游戏。</summary>
    private void UpdatePreview()
    {
        if (type.SelectedValue is not LiveEventType selected) { return; }
        var draft = new TriggerDefinition { Type = selected, GiftName = giftName.Text.Trim(), GiftId = giftId.Text.Trim(), MinGiftUnitValue = long.TryParse(giftMinimum.Text, out var min) ? min : 0, MaxGiftUnitValue = long.TryParse(giftMaximum.Text, out var max) ? max : 0, Threshold = int.TryParse(threshold.Text, out var n) && n > 0 ? n : 1, Keywords = SplitKeywords() };
        error.Text = "";
        summary.Text = RuleDisplay.Trigger(draft) + " → " + RuleDisplay.Effect(rule.Root);
    }

    /// <summary>解析可见关键词。</summary>
    private List<string> SplitKeywords() => keywords.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>校验后保存完整单效果；输入错误不关闭窗口。</summary>
    private void SaveRule()
    {
        try
        {
            rule.Trigger.Type = (LiveEventType)type.SelectedValue;
            rule.Trigger.Keywords = SplitKeywords(); rule.Trigger.GiftName = giftName.Text.Trim(); rule.Trigger.GiftId = giftId.Text.Trim();
            rule.Trigger.MinGiftUnitValue = long.Parse(giftMinimum.Text, CultureInfo.InvariantCulture);
            rule.Trigger.MaxGiftUnitValue = long.Parse(giftMaximum.Text, CultureInfo.InvariantCulture);
            rule.Trigger.Threshold = int.Parse(threshold.Text, CultureInfo.InvariantCulture);
            rule.CooldownSeconds = double.Parse(cooldown.Text, CultureInfo.InvariantCulture); rule.UserCooldownSeconds = double.Parse(userCooldown.Text, CultureInfo.InvariantCulture);
            rule.MaximumPerEvent = int.Parse(perEvent.Text, CultureInfo.InvariantCulture); rule.Enabled = enabled.IsChecked == true;
            rule.Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : RuleDisplay.Trigger(rule.Trigger) + " → " + RuleDisplay.Effect(rule.Root);
            var errors = ProfileStore.ValidateRule(rule);
            if (errors.Count > 0) { error.Text = string.Join("；", errors.Take(2)); return; }
            Result = rule; DialogResult = true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            error.Text = "请检查数量、冷却和次数，填写有效数字。";
        }
    }

    /// <summary>轻量步骤标题。</summary>
    private static TextBlock Heading(string number, string text) => new() { Text = number + "  " + text, FontSize = 18, FontWeight = FontWeights.SemiBold };
}
