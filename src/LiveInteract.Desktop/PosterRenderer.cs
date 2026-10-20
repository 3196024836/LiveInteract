using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LiveInteract.Contracts;
using LiveInteract.Core;

namespace LiveInteract.Desktop;

/// <summary>按当前规则生成透明直播说明图：只依赖规则本身与随包内置的官方礼物图标。</summary>
internal static class PosterRenderer
{
    private const double PageWidth = 720, PageHeight = 1190, Left = 52, Right = 656, TriggerX = 56, EffectStart = 300, EffectLimit = 656, RowStep = 33;
    private const double TriggerWidth = EffectStart - TriggerX - 16;
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly Typeface Font = new("Microsoft YaHei UI");
    private static readonly Brush Ink = ColorBrush("#F4FBFF"), Accent = ColorBrush("#7FE9D0"), Ally = ColorBrush("#9BE8CB"), Enemy = ColorBrush("#FFA69B"), Muted = ColorBrush("#93A8B6"), Faint = ColorBrush("#5B7080");
    /// <summary>礼物名到随包图标的对应关系；没有图标的礼物只显示文字。</summary>
    private static readonly Dictionary<string, string> GiftIcons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["小心心"] = "heart.png", ["玫瑰"] = "rose.png", ["棒棒糖"] = "lollipop.png", ["给到夯"] = "han.png"
    };

    private sealed record Row(string Trigger, string Effect, Brush EffectBrush, string? Icon);
    private sealed class Section
    {
        public string Title = ""; public List<Row> Rows = new();
    }

    /// <summary>生成 PNG 文件；调用方负责选择路径与提示结果。</summary>
    /// <param name="profile">当前规则配置。</param>
    /// <param name="outputPath">目标 PNG 路径。</param>
    /// <returns>实际写入的文件路径。</returns>
    public static string Render(RuleProfile profile, string outputPath)
    {
        var sections = Build(profile);
        var height = Layout(sections);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { Paint(dc, sections); }
        var bitmap = new RenderTargetBitmap((int)(PageWidth * 2), (int)(Math.Ceiling(height) * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var full = Path.GetFullPath(outputPath); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var file = File.Create(full); encoder.Save(file);
        return full;
    }

    /// <summary>把启用中的规则按事件类型分组，触发词与效果都取自现有规则显示逻辑。</summary>
    private static List<Section> Build(RuleProfile profile)
    {
        var enabled = profile.Rules.Where(rule => rule.Enabled).ToArray();
        var sections = new List<Section>();
        void Add(string title, IEnumerable<RuleDefinition> rules)
        {
            var rows = rules.Select(rule =>
            {
                var effect = RuleDisplay.Effect(rule.Root);
                var icon = rule.Trigger.Type == LiveEventType.Gift && GiftIcons.TryGetValue(rule.Trigger.GiftName, out var file) ? file : null;
                return new Row(TriggerLabel(rule.Trigger), effect, EffectBrush(effect), icon);
            }).ToList();
            if (rows.Count > 0) { sections.Add(new Section { Title = title, Rows = rows }); }
        }
        Add("弹　幕", enabled.Where(rule => rule.Trigger.Type == LiveEventType.Chat));
        Add("点　赞", enabled.Where(rule => rule.Trigger.Type == LiveEventType.Like));
        Add("礼　物", enabled.Where(rule => rule.Trigger.Type == LiveEventType.Gift));
        Add("其　他", enabled.Where(rule => rule.Trigger.Type is not (LiveEventType.Chat or LiveEventType.Like or LiveEventType.Gift)));
        return sections;
    }

    /// <summary>触发列只保留主播和观众一眼能对上的短标签。</summary>
    private static string TriggerLabel(TriggerDefinition trigger) => trigger.Type switch
    {
        LiveEventType.Chat => trigger.Keywords.Count > 0 ? string.Join(" / ", trigger.Keywords) : "任意弹幕",
        LiveEventType.Like => trigger.Threshold > 1 ? "每" + trigger.Threshold + "赞" : "每次点赞",
        LiveEventType.Gift => trigger.GiftName.Length > 0 ? trigger.GiftName : trigger.GiftId.Length > 0 ? "指定礼物" : "任意礼物",
        LiveEventType.Follow => trigger.Threshold > 1 ? "关注 ×" + trigger.Threshold : "关注",
        LiveEventType.Enter => trigger.Threshold > 1 ? "进场 ×" + trigger.Threshold : "进场",
        LiveEventType.Share => trigger.Threshold > 1 ? "分享 ×" + trigger.Threshold : "分享",
        _ => "粉丝团"
    };

    /// <summary>友军与敌军用固定两色，其余用正文色。</summary>
    private static Brush EffectBrush(string effect) => effect.Contains("友军", StringComparison.Ordinal) || effect.Contains("友方", StringComparison.Ordinal) ? Ally : effect.Contains("敌人", StringComparison.Ordinal) || effect.Contains("敌方", StringComparison.Ordinal) ? Enemy : Ink;

    /// <summary>先算高度再画；不足一屏时按固定画布补齐，OBS 里已摆好的位置不会因为规则变少而跳动。</summary>
    private static double Layout(List<Section> sections) => Math.Max(ContentBottom(sections) + 26, PageHeight);

    /// <summary>实际内容底部，页脚与左侧竖线都跟着它走。</summary>
    private static double ContentBottom(List<Section> sections)
    {
        var y = 176.0;
        foreach (var section in sections) { y += 50 + section.Rows.Count * Step(section) + 26; }
        return y;
    }

    /// <summary>带图标的礼物行要留出图标高度，否则相邻图标会叠在一起。</summary>
    private static double Step(Section section) => section.Rows.Any(row => row.Icon != null) ? 62 : RowStep;

    private static void Paint(DrawingContext dc, List<Section> sections)
    {
        var rail = new LinearGradientBrush(Color.FromArgb(255, 127, 233, 208), Color.FromArgb(0, 127, 233, 208), 90);
        dc.DrawRoundedRectangle(rail, null, new Rect(30, 52, 7, 74), 3.5, 3.5);
        Text(dc, "丝之歌", Left + 4, 42, 48, Ink, true);
        Text(dc, "L I V E   I N T E R A C T I O N", Left + 6, 110, 12, Alpha("#7FE9D0", 190));
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(80, 127, 233, 208), Color.FromArgb(0, 127, 233, 208), 0), null, new Rect(Left, 150, Right - Left, 1));
        dc.DrawRectangle(Alpha("#7FE9D0", 45), null, new Rect(24, 42, 1, ContentBottom(sections) - 60));

        var y = 176.0;
        foreach (var section in sections)
        {
            dc.DrawEllipse(Alpha("#7FE9D0", 235), null, new Point(Left + 4, y + 9), 3.4, 3.4);
            Text(dc, section.Title, Left + 18, y, 17, Alpha("#C6D6DF", 225));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(70, 127, 233, 208), Color.FromArgb(0, 127, 233, 208), 0), null, new Rect(Left, y + 28, Right - Left, 1));
            var rowY = y + 50;
            var step = Step(section);
            foreach (var row in section.Rows)
            {
                if (row.Icon != null)
                {
                    Icon(dc, row.Icon, Left, rowY, row.EffectBrush);
                    Text(dc, Fit(row.Trigger, EffectStart - (Left + 74) - 16, 22, 16, true), Left + 74, rowY + 15, 22, Ink, true);
                    var (iconSize, iconText) = FitPair(row.Effect, EffectStart, EffectLimit, 21, 16);
                    TextRight(dc, iconText, EffectLimit, rowY + 15, iconSize, row.EffectBrush);
                }
                else
                {
                    Text(dc, Fit(row.Trigger, TriggerWidth, 21, 16, true), TriggerX, rowY, 21, Accent, true);
                    var (size, text) = FitPair(row.Effect, EffectStart, EffectLimit, 21, 16);
                    TextRight(dc, text, EffectLimit, rowY, size, row.EffectBrush);
                }
                rowY += step;
            }
            y += 50 + section.Rows.Count * step + 26;
        }
        var footer = "按当前规则生成 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Text(dc, footer, Left, y - 8, 14, Faint);
    }

    /// <summary>礼物图标放进圆底，外圈跟着效果颜色。</summary>
    private static void Icon(DrawingContext dc, string file, double x, double y, Brush tint)
    {
        const double size = 52;
        var center = new Point(x + size / 2, y + size / 2);
        dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)), null, center, size / 2 + 6, size / 2 + 6);
        var colour = ((SolidColorBrush)tint).Color;
        dc.DrawEllipse(Alpha("#1E2A36", 235), new Pen(new SolidColorBrush(Color.FromArgb(110, colour.R, colour.G, colour.B)), 1.2), center, size / 2, size / 2);
        var bitmap = Embedded(file); if (bitmap == null) { return; }
        var scale = Math.Min((size - 6) / bitmap.PixelWidth, (size - 6) / bitmap.PixelHeight);
        var width = bitmap.PixelWidth * scale; var height = bitmap.PixelHeight * scale;
        dc.DrawImage(bitmap, new Rect(center.X - width / 2, center.Y - height / 2, width, height));
    }

    /// <summary>读取随程序集打包的官方礼物图标；缺失时只画底色，不伪造图标。</summary>
    private static BitmapImage? Embedded(string file)
    {
        var assembly = typeof(PosterRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream("LiveInteract.Desktop.gift." + file);
        if (stream == null) { return null; }
        var image = new BitmapImage();
        image.BeginInit(); image.StreamSource = stream; image.CacheOption = BitmapCacheOption.OnLoad; image.EndInit(); image.Freeze();
        return image;
    }

    /// <summary>按实际排版宽度收缩字号，仍放不下就截断加省略号。</summary>
    private static string Fit(string text, double maxWidth, double size, double minSize, bool bold)
    {
        var (_, result) = FitPair(text, 0, maxWidth, size, minSize, bold);
        return result;
    }

    private static (double Size, string Text) FitPair(string text, double start, double limit, double size, double minSize, bool bold = false)
    {
        var width = limit - start;
        var current = size;
        while (current > minSize && Measure(text, current, bold) > width) { current -= 1; }
        if (Measure(text, current, bold) <= width) { return (current, text); }
        var trimmed = text;
        while (trimmed.Length > 1 && Measure(trimmed + "…", current, bold) > width) { trimmed = trimmed[..^1]; }
        return (current, trimmed + "…");
    }

    private static double Measure(string text, double size, bool bold)
    {
        var type = bold ? new Typeface(Font.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal) : Font;
        return new FormattedText(text, Zh, FlowDirection.LeftToRight, type, size, Brushes.White, 1).WidthIncludingTrailingWhitespace;
    }

    private static void TextRight(DrawingContext dc, string text, double right, double y, double size, Brush fill)
    {
        var type = Font;
        var formatted = new FormattedText(text, Zh, FlowDirection.LeftToRight, type, size, fill, 1);
        Draw(dc, formatted, new Point(right - formatted.WidthIncludingTrailingWhitespace, y), fill);
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush fill, bool bold = false)
    {
        var type = bold ? new Typeface(Font.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal) : Font;
        var formatted = new FormattedText(text, Zh, FlowDirection.LeftToRight, type, size, fill, 1);
        Draw(dc, formatted, new Point(x, y), fill);
    }

    /// <summary>深色描边保证压在游戏画面上也能读清。</summary>
    private static void Draw(DrawingContext dc, FormattedText formatted, Point origin, Brush fill)
    {
        var shape = formatted.BuildGeometry(origin);
        dc.DrawGeometry(null, new Pen(ColorBrush("#E5141A22"), 3) { LineJoin = PenLineJoin.Round }, shape);
        dc.DrawGeometry(fill, null, shape);
    }

    private static Brush ColorBrush(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    private static SolidColorBrush Alpha(string hex, byte alpha)
    {
        var colour = (Color)ColorConverter.ConvertFromString(hex);
        return new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, colour.R, colour.G, colour.B));
    }
}
