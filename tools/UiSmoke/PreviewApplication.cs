using System.IO;
using System.Reflection;
using System.Windows;

/// <summary>仅载入真实样式，禁止产品启动流程连接游戏、注册热键或创建直播服务。</summary>
internal sealed class HarnessApp:Application
{
    public void InitializeComponent()
    {
        using var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("LiveInteract.TestStyles.xaml")!;
        using var reader=new StreamReader(input);
        var document=System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
        System.Xml.Linq.XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources=document.Root!.Element(ns+"Application.Resources")!;
        var dictionary=new System.Xml.Linq.XElement(ns+"ResourceDictionary",
            new System.Xml.Linq.XAttribute(System.Xml.Linq.XNamespace.Xmlns+"x","http://schemas.microsoft.com/winfx/2006/xaml"),resources.Nodes());
        Resources=(ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dictionary.ToString());
    }
}
