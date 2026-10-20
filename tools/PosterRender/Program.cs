using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
/// <summary>从最终默认配置排版透明直播说明；礼物图标使用抖音官方原图，排版保持一栏式简洁结构。</summary>
internal static class Program
{
    static readonly CultureInfo zh=CultureInfo.GetCultureInfo("zh-CN");
    static readonly Typeface font=new("Microsoft YaHei UI");
    static DrawingContext dc=null!;
    static readonly Brush ink=ColorBrush("#F4FBFF"),accent=ColorBrush("#7FE9D0"),ally=ColorBrush("#9BE8CB"),enemy=ColorBrush("#FFA69B"),muted=ColorBrush("#93A8B6"),faint=ColorBrush("#5B7080");
    static Brush ColorBrush(string v)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(v));
    static SolidColorBrush Alpha(string hex,byte a){var c=(Color)ColorConverter.ConvertFromString(hex);return new SolidColorBrush(Color.FromArgb(a,c.R,c.G,c.B));}
    static JsonArray rules=null!;
    static string root=null!;
    static JsonObject Rule(string id)=>rules.OfType<JsonObject>().Single(r=>(string?)r["Id"]==id);
    static string Arg(string id,string key)=>(string)Rule(id)["Root"]!["Arguments"]![key]!;
    const double Left=52,Right=656,ColTrigger=56,ColEffect=276;
    [STAThread]
    static void Main(string[] args)
    {
        root=System.IO.Path.GetFullPath(args[0]);rules=JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root,"profiles","丝之歌-弹幕助力.json")))!["Rules"]!.AsArray();
        var folder=System.IO.Path.Combine(root,"直播素材","0.6新版");System.IO.Directory.CreateDirectory(folder);
        var visual=new DrawingVisual();using(var context=visual.RenderOpen())
        {
            dc=context;
            Header();
            Section(176,"弹　幕");
            var chat=new (string,string)[]{("小弟","近战友军 ×1"),("远程小弟","远程友军 ×1"),("小怪","近战敌人 ×1"),("远程小怪","远程敌人 ×1"),("回血","生命 +"+Arg("v06-heal","value")),("回蓝 / 回丝","灵丝 +"+Arg("v06-silk","value")),("金币","金币雨"),("大招","灵丝风暴"),("生成怪物＋名字","指定敌人 ×1 · 5秒"),("生成小弟＋名字","指定友军 ×1 · 5秒"),("怪变大 / 怪变小","最近敌人 · 永久叠加")};
            for(var i=0;i<chat.Length;i++)Row(232+i*33,chat[i].Item1,chat[i].Item2,ink);
            Section(628,"点　赞");Row(684,"每100赞","金币雨",ink);
            Section(760,"礼　物");
            Gift(820,"heart.png",(string)Rule("v06-heart")["Trigger"]!["GiftName"]!,"随机普通友军 ×2",ally);
            Gift(886,"rose.png",(string)Rule("v06-note")["Trigger"]!["GiftName"]!,"随机普通敌人 ×1",enemy);
            Gift(952,"lollipop.png",(string)Rule("v06-strong-friend")["Trigger"]!["GiftName"]!,"随机强力友军 ×2",ally);
            Gift(1018,"han.png",(string)Rule("v06-strong-enemy")["Trigger"]!["GiftName"]!,"随机强力敌人 ×1",enemy);
            Text("每份礼物独立触发 · 强力随机可能出现Boss",Left,1092,15,faint);
            Text("友军随传送跟随 · 精准召唤5秒",Left,1116,15,faint);
        }
        var bitmap=new RenderTargetBitmap(1440,2380,192,192,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var output=System.IO.Path.Combine(folder,"直播互动说明-透明.png");using(var stream=System.IO.File.Create(output))encoder.Save(stream);
        Console.WriteLine(output+" 1440x2380 RGBA transparent");
    }

    /// <summary>左侧色条 + 标题 + 英文副标题，作为整张图的唯一标识。</summary>
    static void Header()
    {
        var rail=new LinearGradientBrush(Color.FromArgb(255,127,233,208),Color.FromArgb(0,127,233,208),90);
        dc.DrawRoundedRectangle(rail,null,new Rect(30,52,7,74),3.5,3.5);
        Text("丝之歌",Left+4,42,48,ink,true);
        Text("L I V E   I N T E R A C T I O N",Left+6,110,12,Alpha("#7FE9D0",190));
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(80,127,233,208),Color.FromArgb(0,127,233,208),0),null,new Rect(Left,150,Right-Left,1));
        dc.DrawRectangle(Alpha("#7FE9D0",45),null,new Rect(24,42,1,1088));
    }

    /// <summary>分组标题：小圆点 + 字距标签 + 渐隐细线，不再用满宽实线。</summary>
    static void Section(double y,string label)
    {
        dc.DrawEllipse(Alpha("#7FE9D0",235),null,new Point(Left+4,y+9),3.4,3.4);
        Text(label,Left+18,y,17,Alpha("#C6D6DF",225));
        dc.DrawRectangle(new LinearGradientBrush(Color.FromArgb(70,127,233,208),Color.FromArgb(0,127,233,208),0),null,new Rect(Left,y+28,Right-Left,1));
    }

    /// <summary>两列对齐的普通行：触发词在左，效果右对齐，像菜单一样不用箭头。</summary>
    static void Row(double y,string left,string right,Brush rightBrush)
    {
        Text(left,ColTrigger,y,21,accent,true);
        TextRight(right,Right,y,21,rightBrush);
    }

    /// <summary>礼物行：官方图标 + 名称 + 效果，图标外圈跟随阵营色。</summary>
    static void Gift(double y,string file,string name,string effect,Brush effectBrush)
    {
        Icon(file,Left,y,effectBrush);
        Text(name,Left+74,y+9,22,ink,true);
        TextRight(effect,Right,y+9,21,effectBrush);
    }

    /// <summary>右对齐文本，用实际排版宽度回推起点。</summary>
    static void TextRight(string s,double right,double y,double size,Brush fill,bool bold=false)
    {
        var type=bold?new Typeface(font.FontFamily,FontStyles.Normal,FontWeights.Bold,FontStretches.Normal):font;
        var ft=new FormattedText(s,zh,FlowDirection.LeftToRight,type,size,fill,1);
        var shape=ft.BuildGeometry(new Point(right-ft.WidthIncludingTrailingWhitespace,y));
        dc.DrawGeometry(null,new Pen(ColorBrush("#E5141A22"),3){LineJoin=PenLineJoin.Round},shape);dc.DrawGeometry(fill,null,shape);
    }

    static void Icon(string file,double x,double y,Brush tint)
    {
        const double size=52;
        var center=new Point(x+size/2,y+size/2);
        dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(46,255,255,255),Color.FromArgb(0,255,255,255)),null,center,size/2+6,size/2+6);
        var tintColor=((SolidColorBrush)tint).Color;
        dc.DrawEllipse(Alpha("#1E2A36",235),new Pen(new SolidColorBrush(Color.FromArgb(110,tintColor.R,tintColor.G,tintColor.B)),1.2),center,size/2,size/2);
        var bitmap=new BitmapImage(new Uri(System.IO.Path.Combine(root,"直播素材","官方礼物图标",file)));
        var scale=Math.Min((size-6)/bitmap.PixelWidth,(size-6)/bitmap.PixelHeight);
        var width=bitmap.PixelWidth*scale;var height=bitmap.PixelHeight*scale;
        dc.DrawImage(bitmap,new Rect(center.X-width/2,center.Y-height/2,width,height));
    }

    /// <summary>统一文字样式：深色描边保证在游戏画面上也能读清。</summary>
    static void Text(string s,double x,double y,double size,Brush fill,bool bold=false)
    {
        var type=bold?new Typeface(font.FontFamily,FontStyles.Normal,FontWeights.Bold,FontStretches.Normal):font;
        var ft=new FormattedText(s,zh,FlowDirection.LeftToRight,type,size,fill,1);var shape=ft.BuildGeometry(new Point(x,y));
        dc.DrawGeometry(null,new Pen(ColorBrush("#E5141A22"),3){LineJoin=PenLineJoin.Round},shape);dc.DrawGeometry(fill,null,shape);
    }
}
