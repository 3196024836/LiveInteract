using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>从实际默认规则生成透明直播说明图；中文由字体排版，插画单独保留。</summary>
internal static class Program
{
    static JsonArray rules = null!;
    static readonly CultureInfo zh = CultureInfo.GetCultureInfo("zh-CN");
    static readonly Typeface font = new("Microsoft YaHei UI");
    static readonly Brush white = Brush("#FFF8EE");
    static readonly Brush mint = Brush("#A6F1D2");
    static readonly Brush gold = Brush("#FFCF8B");
    static readonly Brush muted = Brush("#D5DEE0");
    static DrawingContext dc = null!;
    static JsonObject Rule(string id) => rules.OfType<JsonObject>().Single(r => (string?)r["Id"] == id && (bool?)r["Enabled"] == true);
    static string Arg(string id,string key) => (string?)Rule(id)["Root"]!["Arguments"]![key] ?? "";
    static string Dur(string id) => Rule(id)["Root"]!["DurationSeconds"]!.ToString();
    static string Key(string id) => (string)Rule(id)["Trigger"]!["Keywords"]![0]!;
    static Brush Brush(string s) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(s));
    [STAThread]
    static void Main(string[] args)
    {
        var root=System.IO.Path.GetFullPath(args[0]);
        rules=JsonNode.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root,"profiles","丝之歌-弹幕助力.json")))!["Rules"]!.AsArray();
        var output=System.IO.Path.Combine(root,"直播素材","丝之歌-友军整活");System.IO.Directory.CreateDirectory(output);
        var chat=new (string,string)[]{
            (Key("v04-life"),"原生回血 +"+Arg("v04-life","value")),
            (Key("v04-silk"),"补充灵丝 +"+Arg("v04-silk","value")),
            (Key("v04-free-ally"),"机械援军 ×1 · "+Arg("v04-free-ally","lifetime")+"秒"),
            (Key("v04-grow"),"怪物变大 · "+Dur("v04-grow")+"秒 / 条"),
            (Key("v04-shrink"),"怪物变小 · "+Dur("v04-shrink")+"秒 / 条"),
            (Key("v04-copy"),"普通敌人 +"+Arg("v04-copy","count")),
            (Key("v04-clear"),"清敌怪 · 保留原生援军"),
            (Key("v04-calm"),"恢复体型 · 清理整活")};
        var gifts=new (string,string)[]{
            ("小心心","机械小弟 ×"+Arg("v04-heart","count")),
            ("玫瑰","机械小弟 ×"+Arg("v04-rose","count")),
            ("其他 10–99钻","原生机械随从 ×2"),
            ("其他 100–499钻","原生机械随从 ×3"),
            ("其他 ≥500钻","原生机械随从 ×4"),
            ("棒棒糖","敌对木蜂 ×1"),
            ("啤酒","复制普通敌人 +1"),
            ("其他低价 / 未知","机械小弟 ×1")};
        // 最终版本仅使用文字说明，不携带自绘或换皮美术。
        Render(output,"直播互动说明-竖版透明.png",840,1250,2,()=>{
            Text("丝之歌",40,38,58,white,true);
            Text("原生援军 · 整活",42,112,34,mint,true);
            Text("原生随从助战，免费弹幕互动",44,170,21,muted);
            Section("弹幕应援",40,260,760,mint,"直接发送关键词");
            for(int i=0;i<chat.Length;i++)Row(chat[i],48,313+i*43,744,mint,24);
            Section("礼物召唤",40,685,760,gold,"友军持续跨房间跟随");
            for(int i=0;i<gifts.Length;i++)Row(gifts[i],48,735+i*43,744,gold,23);
            Line(42,1092,798,1092,mint,.6);
            Text("每50赞 → 补充灵丝 +3     每5条普通弹幕 → 回血 +1",44,1112,22,white);
            Text("召唤物显示你的昵称 · 变大 / 变小逐条独立计时",44,1151,21,mint);
            Text("帮忙为主，不抢操作。大招默认关闭，可由主播开启。",44,1190,20,muted);
        });
        Render(output,"直播互动说明-横版透明.png",1520,850,1.5,()=>{
            Text("丝之歌 · 原生援军互动",45,28,54,white,true);
            Text("你的弹幕，让冒险看得见",49,103,25,mint);
            Section("弹幕应援",45,195,680,mint,"直接发送关键词");
            Section("礼物召唤",795,195,680,gold,"援军持续跟随");
            for(int i=0;i<8;i++){Row(chat[i],53,252+i*53,664,mint,24);Row(gifts[i],803,252+i*53,664,gold,24);}
            Line(48,710,1472,710,mint,.6);
            Text("每50赞 → 灵丝球 +3     每5条普通弹幕 → 生命球 +1     召唤物头顶显示你的名字",49,735,25,white);
            Text("援军跨房间跟随 · 每条变大 / 变小独立叠层计时 · 帮忙为主，不抢操作",49,786,24,mint);
        });
        System.IO.File.WriteAllText(System.IO.Path.Combine(output,"poster-spec.json"),new JsonObject{
            ["Profile"]="profiles/丝之歌-弹幕助力.json",["EnabledRules"]=rules.Count(r=>(bool?)r!["Enabled"]==true),
            ["Chat"]=new JsonArray(chat.Select(r=>(JsonNode)new JsonObject{["Trigger"]=r.Item1,["Effect"]=r.Item2}).ToArray()),
            ["Gifts"]=new JsonArray(gifts.Select(r=>(JsonNode)new JsonObject{["Trigger"]=r.Item1,["Effect"]=r.Item2}).ToArray())
        }.ToJsonString(new(){WriteIndented=true}));
    }
    static void Render(string folder,string name,int w,int h,double scale,Action draw)
    {
        var visual=new DrawingVisual();using(var context=visual.RenderOpen()){dc=context;draw();}
        var bmp=new RenderTargetBitmap((int)(w*scale),(int)(h*scale),96*scale,96*scale,PixelFormats.Pbgra32);bmp.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bmp));using(var stream=System.IO.File.Create(System.IO.Path.Combine(folder,name)))encoder.Save(stream);
        Console.WriteLine($"{name}: {bmp.PixelWidth}x{bmp.PixelHeight}");
    }
    static void Text(string s,double x,double y,double size,Brush fill,bool bold=false)
    {
        var face=bold?new Typeface(font.FontFamily,FontStyles.Normal,FontWeights.Bold,FontStretches.Normal):font;
        var ft=new FormattedText(s,zh,FlowDirection.LeftToRight,face,size,fill,1.0);
        var g=ft.BuildGeometry(new Point(x,y));
        dc.DrawGeometry(null,new Pen(Brush("#E81A2028"),4){LineJoin=PenLineJoin.Round},g);
        dc.DrawGeometry(fill,null,g);
    }
    static void Section(string s,double x,double y,double width,Brush color,string hint)
    {Text(s,x,y,30,color,true);Text(hint,x+170,y+8,18,muted);Line(x,y+46,x+width,y+46,color,.7);}
    static void Row((string,string) row,double x,double y,double width,Brush color,int size)
    {Text(row.Item1,x,y,size,color,true);Text("→",x+246,y,size,muted);Text(row.Item2,x+287,y,size-1,white);}
    static void Line(double x,double y,double xx,double yy,Brush b,double opacity)
    {dc.PushOpacity(opacity);dc.DrawLine(new Pen(b,1.2),new Point(x,y),new Point(xx,yy));dc.Pop();}
}
