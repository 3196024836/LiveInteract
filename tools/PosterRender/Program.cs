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
    static JsonObject Rule(string id)=>rules.OfType<JsonObject>().Single(r=>(string?)r["Id"]==id);
    static string Arg(string id,string key)=>(string)Rule(id)["Root"]!["Arguments"]![key]!;
    [STAThread]
    static void Main(string[] args)
    {
        var root=System.IO.Path.GetFullPath(args[0]);rules=JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root,"profiles","丝之歌-弹幕助力.json")))!["Rules"]!.AsArray();
        var folder=System.IO.Path.Combine(root,"直播素材","0.6新版");System.IO.Directory.CreateDirectory(folder);
        var visual=new DrawingVisual();using(var context=visual.RenderOpen())
        {
            dc=context;Text("丝之歌",42,24,55,ink,true);Text("直播互动",44,99,25,green);Section("弹幕",44,164,632);
            var chat=new (string,string)[]{("小弟","近战友军 ×1"),("远程小弟","远程友军 ×1"),("小怪","近战敌人 ×1"),("远程小怪","远程敌人 ×1"),("回血","生命 +"+Arg("v06-heal","value")),("回蓝 / 回丝","灵丝 +"+Arg("v06-silk","value")),("金币","金币雨 · "+Arg("v06-coins","count")+"颗"),("大招","原作灵丝风暴"),("生成怪物＋名字","指定敌人 ×1 · 5秒"),("生成小弟＋名字","指定友军 ×1 · 5秒"),("怪变大 / 怪变小","最近敌人 · 永久叠加")};
            for(var i=0;i<chat.Length;i++)Row(chat[i].Item1,chat[i].Item2,220+i*34,23);
            Section("点赞",44,624,632);Row("每100赞","金币雨",680,26);
            Section("礼物",44,748,632);
            Gift("heart",(string)Rule("v06-heart")["Trigger"]!["GiftName"]!,"随机普通友军 ×2",803,green);
            Gift("rose",(string)Rule("v06-note")["Trigger"]!["GiftName"]!,"随机普通敌人 ×1",875,red);
            Gift("lollipop",(string)Rule("v06-strong-friend")["Trigger"]!["GiftName"]!,"随机强力友军 ×2",947,green);
            Gift("hammer",(string)Rule("v06-strong-enemy")["Trigger"]!["GiftName"]!,"随机强力敌人 ×1",1019,red);
            Text("每份礼物独立触发 · 强力随机可能出现Boss",44,1112,20,muted);
            Text("友军随传送跟随 · 精准召唤5秒",44,1148,20,muted);
        }
        var bitmap=new RenderTargetBitmap(1440,2380,192,192,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));var output=System.IO.Path.Combine(folder,"直播互动说明-透明.png");using(var stream=System.IO.File.Create(output))encoder.Save(stream);
        Console.WriteLine(output+" 1440x2380 RGBA transparent");
    }
    static void Gift(string icon,string name,string effect,double y,Brush color)
    {
        GiftIcon(icon,44,y);
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

    /// <summary>统一的圆角图块，保证四件礼物在同一视觉重量上。</summary>
    static void GiftIcon(string kind,double x,double y)
    {
        const double size=58,unit=size/64.0;
        var plate=new LinearGradientBrush(Color.FromArgb(255,47,60,74),Color.FromArgb(255,26,34,44),90);
        dc.DrawRoundedRectangle(plate,new Pen(new SolidColorBrush(Color.FromArgb(38,255,255,255)),1),new Rect(x,y,size,size),13,13);
        dc.PushTransform(new TranslateTransform(x,y));dc.PushTransform(new ScaleTransform(unit,unit));
        switch(kind)
        {
            case "heart":Heart();break;
            case "rose":Rose();break;
            case "lollipop":Lollipop();break;
            default:Hammer();break;
        }
        dc.Pop();dc.Pop();
    }
    static void Heart()
    {
        var heart=new StreamGeometry();
        using(var c=heart.Open())
        {
            c.BeginFigure(new Point(32,53),true,true);
            c.BezierTo(new Point(17,41),new Point(9,33),new Point(9,23),true,false);
            c.BezierTo(new Point(9,15),new Point(16,10),new Point(23,10),true,false);
            c.BezierTo(new Point(28,10),new Point(31,12),new Point(32,16),true,false);
            c.BezierTo(new Point(33,12),new Point(36,10),new Point(41,10),true,false);
            c.BezierTo(new Point(48,10),new Point(55,15),new Point(55,23),true,false);
            c.BezierTo(new Point(55,33),new Point(47,41),new Point(32,53),true,false);
        }
        heart.Freeze();
        var fill=new LinearGradientBrush(Color.FromArgb(255,255,138,168),Color.FromArgb(255,214,36,74),90);
        dc.DrawGeometry(fill,new Pen(new SolidColorBrush(Color.FromArgb(90,90,10,30)),1.4),heart);
        var shine=new SolidColorBrush(Color.FromArgb(150,255,255,255));
        dc.DrawEllipse(shine,null,new Point(22,21),5.4,3.6);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90,255,255,255)),null,new Point(38,16),2.1,1.5);
    }
    static void Rose()
    {
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(255,62,155,99)),3.2),new Point(32,54),new Point(32,34));
        var leaf=new StreamGeometry();
        using(var c=leaf.Open())
        {
            c.BeginFigure(new Point(31,45),true,true);
            c.BezierTo(new Point(20,44),new Point(16,37),new Point(20,31),true,false);
            c.BezierTo(new Point(27,32),new Point(31,37),new Point(31,45),true,false);
        }
        leaf.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(255,104,204,140),Color.FromArgb(255,44,128,80),90),null,leaf);
        // 五片外瓣 + 内层花瓣，避免缩到 58px 后糊成一个球。
        var outer=new SolidColorBrush(Color.FromArgb(255,190,26,62));
        var outerEdge=new Pen(new SolidColorBrush(Color.FromArgb(140,92,6,28)),1);
        for(var i=0;i<5;i++)
        {
            var angle=i*72-90;
            dc.PushTransform(new RotateTransform(angle,32,25));
            dc.DrawEllipse(outer,outerEdge,new Point(32,15.5),6.4,7.4);
            dc.Pop();
        }
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(255,226,44,86)),new Pen(new SolidColorBrush(Color.FromArgb(120,120,10,36)),1),new Point(32,25),8.6,8.2);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(255,247,94,124)),null,new Point(32,25),5.2,4.8);
        var curl=new Pen(new SolidColorBrush(Color.FromArgb(190,120,12,38)),1.3);
        var spiral=new StreamGeometry();
        using(var c=spiral.Open())
        {
            c.BeginFigure(new Point(32,22.4),false,false);
            c.ArcTo(new Point(34.4,26.4),new Size(2.6,2.6),0,true,SweepDirection.Clockwise,true,false);
            c.ArcTo(new Point(29.6,26.4),new Size(4.2,4.2),0,true,SweepDirection.Clockwise,true,false);
        }
        spiral.Freeze();
        dc.DrawGeometry(null,curl,spiral);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120,255,255,255)),null,new Point(27,19.5),2.6,1.7);
    }
    static void Lollipop()
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(255,244,232,208)),null,new Rect(29.5,32,5,22),2.4,2.4);
        dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(255,255,178,205),Color.FromArgb(255,226,58,116)),new Pen(new SolidColorBrush(Color.FromArgb(70,120,10,50)),1.2),new Point(32,23),15.5,15.5);
        var white=new Pen(new SolidColorBrush(Color.FromArgb(225,255,255,255)),2.3){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
        var swirl=new StreamGeometry();
        using(var c=swirl.Open())
        {
            c.BeginFigure(new Point(32,11),false,false);
            c.ArcTo(new Point(32,35),new Size(12,12),0,true,SweepDirection.Clockwise,true,false);
        }
        swirl.Freeze();
        dc.DrawGeometry(null,white,swirl);
        var inner=new StreamGeometry();
        using(var c=inner.Open())
        {
            c.BeginFigure(new Point(32,17),false,false);
            c.ArcTo(new Point(32,29),new Size(6,6),0,true,SweepDirection.Counterclockwise,true,false);
        }
        inner.Freeze();
        dc.DrawGeometry(null,white,inner);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120,255,255,255)),null,new Point(25,17),2.6,1.8);
    }
    static void Hammer()
    {
        // 木柄 + 金属锤头，整体收在 64 框内，缩到 58px 也不会压到图块边缘。
        dc.PushTransform(new RotateTransform(-32,32,32));
        dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromArgb(255,214,158,98),Color.FromArgb(255,150,96,48),0),null,new Rect(29,26,6.5,26),3,3);
        dc.DrawRoundedRectangle(new LinearGradientBrush(Color.FromArgb(255,186,201,214),Color.FromArgb(255,108,124,140),90),new Pen(new SolidColorBrush(Color.FromArgb(90,20,28,36)),1.2),new Rect(15,16,34,15),4,4);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(255,236,245,252)),null,new Rect(17.5,18.5,5.5,10));
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(255,152,170,186)),null,new Rect(40.5,18.5,6,10));
        dc.Pop();
        var sparkle=new SolidColorBrush(Color.FromArgb(235,255,222,140));
        Star(sparkle,new Point(49,42),4.4);Star(sparkle,new Point(46,51),2.6);
    }
    static void Star(Brush brush,Point center,double radius)
    {
        var star=new StreamGeometry();
        using(var c=star.Open())
        {
            c.BeginFigure(new Point(center.X,center.Y-radius),true,true);
            c.LineTo(new Point(center.X+radius*.32,center.Y-radius*.32),true,false);
            c.LineTo(new Point(center.X+radius,center.Y),true,false);
            c.LineTo(new Point(center.X+radius*.32,center.Y+radius*.32),true,false);
            c.LineTo(new Point(center.X,center.Y+radius),true,false);
            c.LineTo(new Point(center.X-radius*.32,center.Y+radius*.32),true,false);
            c.LineTo(new Point(center.X-radius,center.Y),true,false);
            c.LineTo(new Point(center.X-radius*.32,center.Y-radius*.32),true,false);
        }
        star.Freeze();dc.DrawGeometry(brush,null,star);
    }
}
