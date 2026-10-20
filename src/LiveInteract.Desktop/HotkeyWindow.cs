using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LiveInteract.Desktop;

/// <summary>本人按键设置全局快捷键，不模拟或注入任何键盘输入。</summary>
internal sealed class HotkeyWindow : Window
{
    /// <summary>确认后的快捷键。</summary>
    public string? SelectedHotkey { get; private set; }

    /// <summary>捕获一个需要修饰键的组合。</summary>
    /// <param name="current">当前快捷键。</param>
    public HotkeyWindow(string current)
    {
        Title = "修改快捷键"; Width = 450; Height = 255; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(25) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "直接按下新的组合键", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "至少包含 Ctrl、Alt 或 Shift；Esc 取消。", Foreground = Ui.Brush("#A9BFCD"), Margin = new(0, 10, 0, 12) });
        var display = new TextBox { Text = current, IsReadOnly = true, FontSize = 20, HorizontalContentAlignment = HorizontalAlignment.Center }; panel.Children.Add(display);
        var save = new Button { Content = "使用这个快捷键", Style = (Style)FindResource("PrimaryButton"), Margin = new(0, 16, 0, 0), IsEnabled = false }; panel.Children.Add(save);
        var candidate = current;
        PreviewKeyDown += (_, e) =>
        {
            e.Handled = true; if (e.Key == Key.Escape) { Close(); return; }
            var key = e.Key == Key.System ? e.SystemKey : e.Key; var modifiers = Keyboard.Modifiers;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin || modifiers == ModifierKeys.None || modifiers.HasFlag(ModifierKeys.Windows)) { return; }
            candidate = (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + key;
            display.Text = candidate; save.IsEnabled = true;
        };
        save.Click += (_, _) => { SelectedHotkey = candidate; DialogResult = true; };
    }

    /// <summary>解析保存的受限快捷键，不接受桌面系统键。</summary>
    /// <param name="text">快捷键文本。</param>
    /// <returns>Windows 修饰键和虚拟键码。</returns>
    public static (uint Modifiers, uint Key) Parse(string text)
    {
        uint modifiers = 0x4000; // 禁止按住连续触发。
        var parts = text.Split('+');
        foreach (var part in parts.Take(parts.Length - 1)) { modifiers |= part switch { "Ctrl" => 2U, "Alt" => 1U, "Shift" => 4U, _ => throw new FormatException("快捷键修饰键无效。") }; }
        if (parts.Length < 2 || !Enum.TryParse<Key>(parts[^1], out var key) || key is Key.None or Key.LWin or Key.RWin) { throw new FormatException("快捷键必须含修饰键和一个普通按键。"); }
        return (modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
}
