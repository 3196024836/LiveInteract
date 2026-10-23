using System.Collections.Generic;
using System.Linq;
namespace LiveInteract.Contracts;

/// <summary>共享召唤容量规则与逐只补位请求，保持参数、触发者和重试标识稳定。</summary>
public static class SummonQueuePolicy
{
    /// <summary>总上限覆盖双方，旧敌方上限仍只额外限制敌人；0表示该项不限。</summary>
    public static bool HasRoom(int total,int enemies,int amount,bool friendly,int totalLimit,int enemyLimit)
    {
        return amount>0&&(totalLimit<=0||(long)total+amount<=totalLimit)
            &&(friendly||enemyLimit<=0||(long)enemies+amount<=enemyLimit);
    }

    /// <summary>大批召唤逐只补位；同一只重试保留ID，不修改原始参数。</summary>
    public static ActionRequest SingleUnit(ActionRequest source,int index)
    {
        var arguments=new Dictionary<string,string>(source.Arguments);
        if(ActionCatalog.All.First(a=>a.Id==source.ActionId).Parameters.Any(p=>p.Key=="count"))arguments["count"]="1";
        return new ActionRequest
        {
            Id=source.Id+"-queued-"+index,SelectionSeed=source.SelectionSeed.Length>0?source.SelectionSeed:source.Id,SelectionOffset=source.SelectionOffset+index,GameId=source.GameId,ActionId=source.ActionId,Arguments=arguments,
            DurationSeconds=source.DurationSeconds,Stacking=source.Stacking,AllowPersistent=source.AllowPersistent,
            DisplayText=source.DisplayText,JobId=source.JobId,UnitIndex=source.UnitIndex,Triggerer=source.Triggerer,SourceType=source.SourceType
        };
    }
}
