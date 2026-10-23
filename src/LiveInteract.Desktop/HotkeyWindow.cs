using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LiveInteract.Desktop;

/// <summary>本人按键设置全局快捷键，不模拟或注入任何键盘输入。</summary>
internal sealed class HotkeyWindow : Window
{
    private readonly bool allowSingleFunctionKey;
    private readonly TextBox display;
    private readonly Button save;
    private string candidate;
    /// <summary>确认后的快捷键。</summary>
    public string? SelectedHotkey { get; private set; }

    /// <summary>捕获组合键；清除召唤可选择单个功能键。</summary>
    /// <param name="current">当前快捷键。</param>
    /// <param name="allowFunctionKey">清除操作可使用单个功能键。</param>
    public HotkeyWindow(string current, bool allowFunctionKey = false)
    {
        allowSingleFunctionKey=allowFunctionKey;candidate=current;
        Title = "修改快捷键"; Width = 450; Height = allowFunctionKey ? 285 : 255; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(25) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "直接按下新的组合键", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = allowFunctionKey ? "可用 F1～F24 单键，或 Ctrl / Alt / Shift 组合；Esc 取消。" : "至少包含 Ctrl、Alt 或 Shift；Esc 取消。", TextWrapping=TextWrapping.Wrap, Foreground = Ui.Brush("#A9BFCD"), Margin = new(0, 10, 0, 12) });
        display = new TextBox { Text = current, IsReadOnly = true, FontSize = 20, HorizontalContentAlignment = HorizontalAlignment.Center }; panel.Children.Add(display);
        save = new Button { Content = "使用这个快捷键", Style = (Style)FindResource("PrimaryButton"), Margin = new(0, 16, 0, 0), IsEnabled = false }; panel.Children.Add(save);
        PreviewKeyDown += (_, e) =>
        {
            e.Handled = true; if (e.Key == Key.Escape) { Close(); return; }
            var key = e.Key == Key.System ? e.SystemKey : e.Key; var modifiers = Keyboard.Modifiers;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin || modifiers == ModifierKeys.None && (!allowFunctionKey || key < Key.F1 || key > Key.F24) || modifiers.HasFlag(ModifierKeys.Windows)) { return; }
            candidate = (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + key;
            display.Text = candidate; save.IsEnabled = true;
        };
        save.Click += (_, _) => { SelectedHotkey = candidate; DialogResult = true; };
    }

    /// <summary>录键时把已注册键的WM_HOTKEY转为候选，不执行实际清场或暂停。</summary>
    public void RecordRegisteredShortcut(string text)
    {
        try{Parse(text,allowSingleFunctionKey);}
        catch(FormatException){return;}
        candidate=text;display.Text=text;save.IsEnabled=true;
    }

    /// <summary>解析保存的受限快捷键；仅清除召唤允许单个功能键。</summary>
    /// <param name="text">快捷键文本。</param>
    /// <returns>Windows 修饰键和虚拟键码。</returns>
    public static (uint Modifiers, uint Key) Parse(string text, bool allowFunctionKey = false)
    {
        uint modifiers = 0x4000; // 禁止按住连续触发。
        var parts = text.Split('+');
        foreach (var part in parts.Take(parts.Length - 1)) { modifiers |= part switch { "Ctrl" => 2U, "Alt" => 1U, "Shift" => 4U, _ => throw new FormatException("快捷键修饰键无效。") }; }
        if (!Enum.TryParse<Key>(parts[^1], out var key) || !Enum.IsDefined(key) || key is Key.None or Key.LWin or Key.RWin || parts.Length < 2 && (!allowFunctionKey || key < Key.F1 || key > Key.F24)) { throw new FormatException("快捷键需要修饰键；清除召唤可使用F1～F24单键。"); }
        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
}
