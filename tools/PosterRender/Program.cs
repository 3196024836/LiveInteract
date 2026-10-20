using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
/// <summary>从最终默认配置排版透明直播说明；礼物图标为本工具绘制示意图标，不使用平台官方美术。</summary>
internal static class Program
{
    static readonly CultureInfo zh=CultureInfo.GetCultureInfo("zh-CN");
    static readonly Typeface font=new("Microsoft YaHei UI");
    static DrawingContext dc=null!;
    static readonly Brush ink=ColorBrush("#FFF9EF"),green=ColorBrush("#B7E8D9"),red=ColorBrush("#FFB7AD"),muted=ColorBrush("#D1D9DA");
    static Brush ColorBrush(string v)=>new SolidColorBrush((Color)ColorConverter.ConvertFromString(v));
    static JsonArray rules=null!;
    static string root=null!;
    static JsonObject Rule(string id)=>rules.OfType<JsonObject>().Single(r=>(string?)r["Id"]==id);
    static string Arg(string id,string key)=>(string)Rule(id)["Root"]!["Arguments"]![key]!;
    [STAThread]
    static void Main(string[] args)
    {
        root=System.IO.Path.GetFullPath(args[0]);rules=JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root,"profiles","丝之歌-弹幕助力.json")))!["Rules"]!.AsArray();
        var folder=System.IO.Path.Combine(root,"直播素材","0.6新版");System.IO.Directory.CreateDirectory(folder);
        var visual=new DrawingVisual();using(var context=visual.RenderOpen())
        {
            dc=context;Text("丝之歌",42,24,55,ink,true);Text("直播互动",44,99,25,green);Section("弹幕",44,164,632);
            var chat=new (string,string)[]{("小弟","近战友军 ×1"),("远程小弟","远程友军 ×1"),("小怪","近战敌人 ×1"),("远程小怪","远程敌人 ×1"),("回血","生命 +"+Arg("v06-heal","value")),("回蓝 / 回丝","灵丝 +"+Arg("v06-silk","value")),("金币","金币雨"),("大招","灵丝风暴"),("生成怪物＋名字","指定敌人 ×1 · 5秒"),("生成小弟＋名字","指定友军 ×1 · 5秒"),("怪变大 / 怪变小","最近敌人 · 永久叠加")};
            for(var i=0;i<chat.Length;i++)Row(chat[i].Item1,chat[i].Item2,220+i*34,23);
            Section("点赞",44,624,632);Row("每100赞","金币雨",680,26);
            Section("礼物",44,748,632);
            Gift("heart.png",(string)Rule("v06-heart")["Trigger"]!["GiftName"]!,"随机普通友军 ×2",803,green);
            Gift("rose.png",(string)Rule("v06-note")["Trigger"]!["GiftName"]!,"随机普通敌人 ×1",875,red);
            Gift("lollipop.png",(string)Rule("v06-strong-friend")["Trigger"]!["GiftName"]!,"随机强力友军 ×2",947,green);
            Gift("han.png",(string)Rule("v06-strong-enemy")["Trigger"]!["GiftName"]!,"随机强力敌人 ×1",1019,red);
            Text("每份礼物独立触发 · 强力随机可能出现Boss",44,1112,20,muted);
            Text("友军随传送跟随 · 精准召唤5秒",44,1148,20,muted);
        }
        var bitmap=new RenderTargetBitmap(1440,2380,192,192,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var output=System.IO.Path.Combine(folder,"直播互动说明-透明.png");using(var stream=System.IO.File.Create(output))encoder.Save(stream);
        Console.WriteLine(output+" 1440x2380 RGBA transparent");
    }
    static void Gift(string image,string name,string effect,double y,Brush color)
    {
        ImageIcon(image,44,y);
        Text(name,116,y+13,25,ink,true);
        Text("→",302,y+13,24,muted);Text(effect,350,y+14,23,color);
    }
    static void Row(string left,string right,double y,double size){Text(left,48,y,size,green,true);Text("→",302,y,size,muted);Text(right,350,y,size,ink);}
    static void Section(string name,double x,double y,double width){Text(name,x,y,29,ink,true);dc.PushOpacity(.6);dc.DrawLine(new Pen(green,1),new Point(x,y+43),new Point(x+width,y+43));dc.Pop();}
    static void Text(string s,double x,double y,double size,Brush fill,bool bold=false)
    {
        var type=bold?new Typeface(font.FontFamily,FontStyles.Normal,FontWeights.Bold,FontStretches.Normal):font;
        var ft=new FormattedText(s,zh,FlowDirection.LeftToRight,type,size,fill,1);var shape=ft.BuildGeometry(new Point(x,y));
        dc.DrawGeometry(null,new Pen(ColorBrush("#E51C2026"),3.5){LineJoin=PenLineJoin.Round},shape);dc.DrawGeometry(fill,null,shape);
    }

    /// <summary>抖音开放平台公开的官方礼物原图，按图块尺寸等比放进去。</summary>
    static void ImageIcon(string file,double x,double y)
    {
        const double size=58;
        var plate=new LinearGradientBrush(Color.FromArgb(255,47,60,74),Color.FromArgb(255,26,34,44),90);
        dc.DrawRoundedRectangle(plate,new Pen(new SolidColorBrush(Color.FromArgb(38,255,255,255)),1),new Rect(x,y,size,size),13,13);
        var bitmap=new BitmapImage(new Uri(System.IO.Path.Combine(root,"直播素材","官方礼物图标",file)));
        var scale=Math.Min((size-8)/bitmap.PixelWidth,(size-8)/bitmap.PixelHeight);
        var width=bitmap.PixelWidth*scale;var height=bitmap.PixelHeight*scale;
        dc.DrawImage(bitmap,new Rect(x+(size-width)/2,y+(size-height)/2,width,height));
    }
}
