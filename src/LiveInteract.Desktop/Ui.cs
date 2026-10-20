using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveInteract.Contracts;

namespace LiveInteract.Desktop;

/// <summary>小型表单控件工厂，只处理界面，不执行规则或游戏动作。</summary>
internal static class Ui
{
    /// <summary>创建字段标签。</summary>
    /// <param name="text">标签内容。</param>
    /// <returns>标签控件。</returns>
    public static TextBlock Label(string text) => new() { Text = text, Foreground = Brush("#A7BCCB"), FontSize = 13, Margin = new(0, 13, 0, 6) };

    /// <summary>创建包含标签的文本输入。</summary>
    /// <param name="panel">容器。</param>
    /// <param name="label">标签。</param>
    /// <param name="value">当前值。</param>
    /// <returns>输入框。</returns>
    public static TextBox Field(Panel panel, string label, string value)
    {
        panel.Children.Add(Label(label));
        var input = new TextBox { Text = value, MinWidth = 80 }; // 字段输入。
        panel.Children.Add(input);
        return input;
    }

    /// <summary>创建中文显示但保持稳定值的选择框。</summary>
    /// <typeparam name="T">实际值类型。</typeparam>
    /// <param name="panel">可选容器。</param>
    /// <param name="label">可选标签。</param>
    /// <param name="items">值与中文名称。</param>
    /// <param name="selected">当前值。</param>
    /// <returns>选择框。</returns>
    public static ComboBox Choice<T>(Panel? panel, string label, IEnumerable<(T Value, string Name)> items, T selected)
    {
        var combo = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Value", ItemsSource = items.Select(item => new Pick<T>(item.Value, item.Name)).ToList(), SelectedValue = selected }; // 展示与存储值分离。
        if (panel != null)
        {
            if (label.Length > 0) { panel.Children.Add(Label(label)); }
            panel.Children.Add(combo);
        }
        return combo;
    }

    /// <summary>生成当前动作的所有参数与持续时间控件，输入直接更新编辑副本。</summary>
    /// <param name="panel">参数容器。</param>
    /// <param name="step">正在编辑的副本。</param>
    /// <param name="status">真实游戏目录。</param>
    public static void ActionFields(StackPanel panel, ActionStep step, GameStatus? status)
    {
        panel.Children.Clear();
        var descriptor = ActionCatalog.All.FirstOrDefault(item => item.Id == step.ActionId);
        if (descriptor == null) { panel.Children.Add(Label("效果不存在，请重新选择。")); return; }
        var position = new StackPanel { Margin = new(8, 0, 8, 12) };
        foreach (var parameter in descriptor.Parameters)
        {
            var key = parameter.Key;
            if (!step.Arguments.ContainsKey(key)) { step.Arguments[key] = parameter.DefaultValue; }
            var target = key is "offsetX" or "offsetY" ? position : panel;
            target.Children.Add(Label(key == "enemy" ? "作用对象（留空为最近 / 当前 Boss）" : parameter.Label));
            if (parameter.Kind is ParameterKind.Choice or ParameterKind.Boolean)
            {
                var values = parameter.Kind == ParameterKind.Boolean ? new[] { ("true", "开启"), ("false", "关闭") } : parameter.Choices.Select(item => (item.Key, item.Value));
                var choice = Choice<string>(null, "", values, step.Arguments[key]); choice.Tag = key;
                choice.SelectionChanged += (_, _) => step.Arguments[key] = choice.SelectedValue?.ToString() ?? parameter.DefaultValue;
                target.Children.Add(choice);
            }
            else if (parameter.Kind == ParameterKind.Text && key == "enemy")
            {
                var values = new[] { ("", "自动选择最近 / 当前 Boss") }.Concat((status?.EnemyIds ?? []).Select(value =>
                {
                    var label = MonsterCatalog.All.FirstOrDefault(m => value.StartsWith(m.ObjectName, StringComparison.Ordinal))?.Name ?? value;
                    return (value, label);
                }));
                if (step.Arguments[key].Length > 0 && !values.Any(item => item.Item1 == step.Arguments[key])) { values = values.Append((step.Arguments[key], step.Arguments[key] + "（未在当前场景找到）")); }
                var choice = Choice<string>(null, "", values, step.Arguments[key]); choice.Tag = key;
                choice.SelectionChanged += (_, _) => step.Arguments[key] = choice.SelectedValue?.ToString() ?? ""; target.Children.Add(choice);
            }
            else if (parameter.Kind == ParameterKind.Text && key is "tool" or "crest" or "scene" or "event" or "asset")
            {
                IEnumerable<string> values = key switch
                {
                    "tool" => status?.ToolIds ?? [], "crest" => status?.CrestIds ?? [], "scene" => status?.SceneIds ?? [],
                    "asset" => status?.EnemyAssetIds ?? [], "event" => status?.SkillEvents.Where(item => item.StartsWith(step.Arguments.GetValueOrDefault("fsm", "spellControl") + ":")).Select(item => item[(item.IndexOf(':') + 1)..]) ?? [], _ => []
                };
                var input = new ComboBox { IsEditable = true, ItemsSource = values.ToArray(), Text = step.Arguments[key], Tag = key };
                input.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => step.Arguments[key] = input.Text)); target.Children.Add(input);
            }
            else
            {
                var input = new TextBox { Text = step.Arguments[key], Tag = key, ToolTip = parameter.Kind is ParameterKind.Number or ParameterKind.Integer ? "可填写范围：" + parameter.Minimum + " — " + parameter.Maximum : null };
                input.TextChanged += (_, _) => step.Arguments[key] = input.Text.Trim(); target.Children.Add(input);
            }
        }
        if (position.Children.Count > 0) { panel.Children.Add(new Expander { Header = "生成位置（可选）", Content = position, Foreground = Brush("#B5CBD7"), Margin = new(0, 14, 0, 0) }); }
        if (descriptor.IsTemporary)
        {
            var duration = Field(panel, step.ActionId is "enemy.growStack" or "enemy.shrinkStack" ? "持续时间（秒，0＝永久外观）" : "持续时间（秒）", step.DurationSeconds.ToString(CultureInfo.InvariantCulture)); duration.Tag = "duration";
            duration.TextChanged += (_, _) => step.DurationSeconds = double.TryParse(duration.Text, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
            var stacking = Choice(panel, "再次触发", new[] { (EffectStacking.Refresh, "刷新时间"), (EffectStacking.Extend, "延长时间"), (EffectStacking.Stack, "叠加效果") }, step.Stacking);
            stacking.SelectionChanged += (_, _) => step.Stacking = stacking.SelectedValue is EffectStacking value ? value : EffectStacking.Refresh;
        }
    }

    /// <summary>读取画刷，不创建重复的图像资源。</summary>
    /// <param name="color">颜色。</param>
    /// <returns>画刷。</returns>
    public static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));

    /// <summary>选择框中稳定值和中文显示名称。</summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="Value">实际值。</param>
    /// <param name="Name">显示名。</param>
    public sealed record Pick<T>(T Value, string Name);

    /// <summary>中文事件名称，避免要求主播理解内部枚举。</summary>
    public static (LiveEventType Value, string Name)[] EventTypes => [(LiveEventType.Chat, "弹幕"), (LiveEventType.Gift, "礼物"), (LiveEventType.Like, "点赞"), (LiveEventType.Follow, "关注"), (LiveEventType.Enter, "进场"), (LiveEventType.Share, "分享"), (LiveEventType.FanClub, "粉丝团")];

}
