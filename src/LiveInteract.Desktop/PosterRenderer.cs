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
    // 细长条 + 单行 + 短文案：触发词在左、效果右对齐，所有「· 后缀」与括号补充都不上图。
    private const double PageWidth = 300, PageHeight = 760, Left = 22, Right = 278;
    private const double TriggerX = 24, TriggerMax = 88, EffectLeft = 118, EffectRight = 278;
    private const double RowStep = 26, GiftStep = 34, GiftIconSize = 24;
    private const double TriggerSize = 12.5, EffectSize = 11.5, EffectMin = 9.5;
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
                var effect = Short(RuleDisplay.Effect(rule.Root));
                // 分档与倍率分别显示；旧版池不伪装成新分档池。
                if (rule.Root.ActionId is "summon.random" or "summon.random.ally" or "summon.random.enemy")
                {
                    var power = double.TryParse(rule.Root.Arguments.GetValueOrDefault("power"), NumberStyles.Float, CultureInfo.InvariantCulture, out var strength) ? strength : 1;
                    var kind = SummonCommands.IsFriendly(rule.Root.ActionId,rule.Root.Arguments) ? "友军" : "敌人";
                    var pool = rule.Root.Arguments.GetValueOrDefault("pool", "ordinary") switch
                    {
                        "weak" => "弱怪",
                        "strongBoss" => "强怪/Boss",
                        "ordinary" => "旧普通池",
                        "all" => "旧全池",
                        _ => "未知池"
                    };
                    effect = "随机" + pool + kind + " ×" + rule.Root.Arguments.GetValueOrDefault("count", "1");
                    if (power != 1) { effect += " · " + power.ToString("0.##", CultureInfo.InvariantCulture) + "×"; }
                }
                if(rule.Root.ActionId=="ally.copy-next")effect="下次遇敌复制友军 · 永久";
                // 未完成验收的默认候选不能在说明图上看起来已开放。
                if (MonsterCatalog.Find(rule.Root.ActionId) is { SummonEligible: false }) { effect += " · 未开放"; }
                // 精准召唤有存在时限，这条必须留在图上。
                if (rule.Root.ActionId is "summon.named" or "summon.named.enemy" or "summon.named.ally" && rule.Root.Arguments.GetValueOrDefault("lifetime") is { Length: > 0 } lifetime && lifetime != "0")
                {
                    effect += " · " + lifetime + "秒";
                }
                var icon = rule.Trigger.Type == LiveEventType.Gift && GiftIcons.TryGetValue(rule.Trigger.GiftName, out var file) ? file : null;
                var brush = SummonCommands.IsSummon(rule.Root.ActionId)
                    ? (SummonCommands.IsFriendly(rule.Root.ActionId, rule.Root.Arguments) ? Ally : Enemy)
                    : EffectBrush(effect);
                return new Row(TriggerLabel(rule), effect, brush, icon);
            }).ToList();
            if (rows.Count > 0) { sections.Add(new Section { Title = title, Rows = rows }); }
        }
        Add("弹　幕", enabled.Where(rule => rule.Trigger.Type == LiveEventType.Chat));
        Add("点　赞", enabled.Where(rule => rule.Trigger.Type == LiveEventType.Like));
        Add("礼　物", enabled.Where(rule => rule.Trigger.Type == LiveEventType.Gift));
        Add("其　他", enabled.Where(rule => rule.Trigger.Type is not (LiveEventType.Chat or LiveEventType.Like or LiveEventType.Gift)));
        return sections;
    }

    /// <summary>效果列只留主体：去掉「· 持续跟随 / · 普通怪池 / · 永久」这类后缀、括号说明、末尾缩放倍率和“原生”前缀。</summary>
    private static string Short(string effect)
    {
        var cut = effect.IndexOf(" · ", StringComparison.Ordinal);
        if (cut > 0) { effect = effect[..cut]; }
        effect = effect.Replace("原生", "");
        if (effect.StartsWith("施放", StringComparison.Ordinal)) { effect = effect[2..]; } // 丝技只显示技能名
        effect = System.Text.RegularExpressions.Regex.Replace(effect, "（[^）]*）", "").Trim();
        effect = System.Text.RegularExpressions.Regex.Replace(effect, @"\s*\d+\.\d+\s*$", "").Trim();
        return effect;
    }

    /// <summary>触发列只保留主播和观众一眼能对上的短标签；精准召唤必须写出“＋名字”才知道怎么触发。</summary>
    private static string TriggerLabel(RuleDefinition rule)
    {
        var trigger = rule.Trigger;
        if(rule.Root.ActionId=="ally.copy-next"&&trigger.Type==LiveEventType.Gift&&trigger.MinGiftUnitValue==trigger.MaxGiftUnitValue&&trigger.MinGiftUnitValue>0)
            return trigger.GiftName+"（"+trigger.MinGiftUnitValue+"钻）";
        if (rule.Root.ActionId is "summon.named" or "summon.named.enemy" or "summon.named.ally" && trigger.Type == LiveEventType.Chat)
        {
            return (trigger.Keywords.FirstOrDefault() ?? "生成") + "＋名字";
        }
        return trigger.Type switch
    {
        LiveEventType.Chat => trigger.Keywords.Count > 0 ? string.Join(" / ", trigger.Keywords) : "任意弹幕",
        LiveEventType.Like => trigger.Threshold > 1 ? "每" + trigger.Threshold + "赞" : "每次点赞",
        LiveEventType.Gift => trigger.GiftName.Length > 0 ? trigger.GiftName : trigger.GiftId.Length > 0 ? "指定礼物" : "任意礼物",
        LiveEventType.Follow => trigger.Threshold > 1 ? "关注 ×" + trigger.Threshold : "关注",
        LiveEventType.Enter => trigger.Threshold > 1 ? "进场 ×" + trigger.Threshold : "进场",
        LiveEventType.Share => trigger.Threshold > 1 ? "分享 ×" + trigger.Threshold : "分享",
        _ => "粉丝团"
    };
    }

    /// <summary>友军与敌军用固定两色，其余用正文色。</summary>
    private static Brush EffectBrush(string effect) => effect.Contains("友军", StringComparison.Ordinal) || effect.Contains("友方", StringComparison.Ordinal) ? Ally : effect.Contains("敌人", StringComparison.Ordinal) || effect.Contains("敌方", StringComparison.Ordinal) ? Enemy : Ink;

    /// <summary>先算高度再画；不足一屏时按固定画布补齐，OBS 里已摆好的位置不会因为规则变少而跳动。</summary>
    private static double Layout(List<Section> sections) => Math.Max(ContentBottom(sections) + 26, PageHeight);

    /// <summary>实际内容底部，页脚与左侧竖线都跟着它走。</summary>
    private static double ContentBottom(List<Section> sections)
    {
        var y = 94.0;
        foreach (var section in sections) { y += 31 + section.Rows.Count * Step(section) + 20; }
        return y;
    }

    /// <summary>礼物行要留出图标高度，其余行保持紧凑单行。</summary>
    private static double Step(Section section) => section.Rows.Any(row => row.Icon != null) ? GiftStep : RowStep;

    private static void Paint(DrawingContext dc, List<Section> sections)
    {
        var rail = new LinearGradientBrush(Color.FromArgb(255, 127, 233, 208), Color.FromArgb(0, 127, 233, 208), 90);
        dc.DrawRoundedRectangle(rail, null, new Rect(12, 20, 4, 42), 2, 2);
        Text(dc, "丝之歌", 22, 12, 26, Ink, true);
        Text(dc, "L I V E   I N T E R A C T I O N", 23, 48, 8, Alpha("#7FE9D0", 190));
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(80, 127, 233, 208), Color.FromArgb(0, 127, 233, 208), 0), null, new Rect(Left, 72, Right - Left, 1));
        dc.DrawRectangle(Alpha("#7FE9D0", 45), null, new Rect(12, 16, 1, ContentBottom(sections) - 26));

        var y = 94.0;
        foreach (var section in sections)
        {
            dc.DrawEllipse(Alpha("#7FE9D0", 235), null, new Point(Left + 2, y + 6), 2.2, 2.2);
            Text(dc, section.Title, Left + 10, y, 11.5, Alpha("#C6D6DF", 225));
            dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(70, 127, 233, 208), Color.FromArgb(0, 127, 233, 208), 0), null, new Rect(Left, y + 18, Right - Left, 1));
            var rowY = y + 31;
            var step = Step(section);
            foreach (var row in section.Rows)
            {
                if (row.Icon != null)
                {
                    Icon(dc, row.Icon, Left, rowY, row.EffectBrush);
                    var nameX = Left + GiftIconSize + 8;
                    Text(dc, Fit(row.Trigger, EffectLeft - nameX - 6, TriggerSize + 0.5, 10, true), nameX, rowY + 5, TriggerSize + 0.5, Ink, true);
                    var (giftSize, giftText) = FitPair(row.Effect, EffectLeft, EffectRight, EffectSize, EffectMin);
                    TextRight(dc, giftText, EffectRight, rowY + 6, giftSize, row.EffectBrush);
                }
                else
                {
                    Text(dc, Fit(row.Trigger, TriggerMax, TriggerSize, 10, true), TriggerX, rowY, TriggerSize, Accent, true);
                    var (effectSize, effectText) = FitPair(row.Effect, EffectLeft, EffectRight, EffectSize, EffectMin);
                    TextRight(dc, effectText, EffectRight, rowY, effectSize, row.EffectBrush);
                }
                rowY += step;
            }
            y += 31 + section.Rows.Count * step + 20;
        }
        var footer = "按当前规则生成 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        Text(dc, footer, Left, y + 2, 9, Faint);
    }

    /// <summary>礼物图标放进圆底，外圈跟着效果颜色。</summary>
    private static void Icon(DrawingContext dc, string file, double x, double y, Brush tint)
    {
        const double size = GiftIconSize;
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
