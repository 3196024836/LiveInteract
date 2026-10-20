using System.Windows;
using System.Windows.Controls;
using LiveInteract.Contracts;
using LiveInteract.Core;
namespace LiveInteract.Desktop;
/// <summary>只改四条礼物触发名称/ID，效果和每份整数触发保持不变；取消不写回。</summary>
internal sealed class GiftBindingsWindow : Window
{
    private readonly List<(RuleDefinition Rule,ComboBox Name,TextBox Id)> rows=new();
    public Dictionary<string,(string Name,string Id)>? Result { get; private set; }
    public GiftBindingsWindow(IEnumerable<RuleDefinition> rules,IEnumerable<string> observed)
    {
        Title="礼物快捷绑定";Width=680;Height=600;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel{Margin=new Thickness(24)};Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        panel.Children.Add(new TextBlock{Text="每1份礼物，对应一次效果",FontSize=24,Margin=new(0,0,0,10)});
        panel.Children.Add(new TextBlock{Text="名称从实际直播间选择或直接填写；ID可留空。改名后记得同步直播图。",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,16)});
        var names=observed.Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct().ToArray();
        foreach(var rule in rules.Where(r=>r.Trigger.Type==LiveEventType.Gift))
        {
            panel.Children.Add(new TextBlock{Text=rule.Name+" · "+RuleDisplay.Effect(rule.Root),FontWeight=FontWeights.SemiBold,Margin=new(0,10,0,5)});
            var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
            var name=new ComboBox{IsEditable=true,Text=rule.Trigger.GiftName,ItemsSource=names,Margin=new(0,0,10,0),ToolTip="实际礼物名称"};
            var id=new TextBox{Text=rule.Trigger.GiftId,ToolTip="网页事件中的真实礼物ID，可留空"};grid.Children.Add(name);grid.Children.Add(id);Grid.SetColumn(id,1);panel.Children.Add(grid);rows.Add((rule,name,id));
        }
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new(0,14,0,0),Foreground=Ui.Brush("#F4B38F")};panel.Children.Add(error);
        var save=new Button{Content="保存四种礼物绑定",Margin=new(0,16,0,0),HorizontalAlignment=HorizontalAlignment.Right};panel.Children.Add(save);
        save.Click+=(_,_)=>
        {
            if(rows.Any(r=>string.IsNullOrWhiteSpace(r.Name.Text)&&string.IsNullOrWhiteSpace(r.Id.Text))){error.Text="每条至少填写名称或实际ID，留空会误匹配所有礼物，因此不能保存。";return;}
            var keys=rows.Select(r=>r.Id.Text.Trim().Length>0?"id:"+r.Id.Text.Trim():"name:"+r.Name.Text.Trim()).ToArray();
            if(keys.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=keys.Length){error.Text="四种礼物不能绑定同一个名称/ID，否则会互相抢触发。";return;}
            Result=rows.ToDictionary(r=>r.Rule.Id,r=>(r.Name.Text.Trim(),r.Id.Text.Trim()));DialogResult=true;
        };
    }
}
