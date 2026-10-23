using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;
/// <summary>下一次遇敌复制受稳定池限制，并保留原来的礼物规则。</summary>
public sealed class NextEncounterCopyTests
{
    private static RuleProfile Profile()
    {
        var d=new DirectoryInfo(AppContext.BaseDirectory);
        while(d!=null&&!File.Exists(Path.Combine(d.FullName,"LiveInteract.sln")))d=d.Parent;
        return ProfileStore.Read(File.ReadAllText(Path.Combine(d!.FullName,"profiles","丝之歌-弹幕助力.json")),false);
    }
    [Fact] public void CopyActionIsAvailableWithoutPermanentSavePermission()
    {
        var descriptor=Assert.Single(ActionCatalog.UserFacing,a=>a.Id=="ally.copy-next");
        Assert.False(descriptor.IsPersistent);
        Assert.Empty(ActionValidation.Validate(new(){ActionId="ally.copy-next",AllowPersistent=false}));
    }
    [Fact] public void CopyEditorExplainsWaitingWithoutAnEmptyLifetime()
    {
        var text=RuleDisplay.Effect(new(){ActionId="ally.copy-next"});
        Assert.Contains("无目标保留",text);Assert.DoesNotContain(" 秒",text);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(19)][InlineData(166)]
    public void BigBeerGiftQueuesOneCopyForEachGiftRegardlessOfPriceMetadata(long value)
    {
        var p=Profile();Assert.Equal(18,p.Rules.Count);
        var match=Assert.Single(new RuleEngine(reliable:true).Evaluate(new(){Id="copy",Type=LiveEventType.Gift,GiftName="大啤酒",GiftUnitValue=value,Count=3,Nickname="送礼人"},p.Rules));
        Assert.Equal("ally.copy-next",match.Rule.Root.ActionId);Assert.Equal(3,match.Count);
        Assert.Equal(0,match.Rule.Root.DurationSeconds);
        Assert.Equal(0,match.Rule.Trigger.MinGiftUnitValue);Assert.Equal(0,match.Rule.Trigger.MaxGiftUnitValue);
    }
    [Theory][InlineData("抖音")][InlineData("啤酒")][InlineData("大啤酒特效")][InlineData("未知礼物")]
    public void OldOrDifferentGiftNamesDoNotTriggerCopy(string name)
    {
        Assert.Empty(new RuleEngine(reliable:true).Evaluate(new(){Id="not-beer",Type=LiveEventType.Gift,GiftName=name,GiftUnitValue=1},Profile().Rules));
    }
    [Theory][InlineData("颅潜虫","spawn.bone-goomba")][InlineData("Bone Goomba(Clone)","spawn.bone-goomba")][InlineData("Bone Goomba [LiveInteract]","spawn.bone-goomba")]
    public void ExactKnownBodyMapsToApprovedKind(string source,string id)=>Assert.Equal(id,SummonCommands.CopyableEncounter(source)?.Id);
    [Theory][InlineData("spawn.bone-worm-blueturret")][InlineData("spawn.seth")][InlineData("spawn.lifeblood-fly")][InlineData("not-a-monster")]
    public void UnsafeOrUnknownEncountersAreSkippedNotSubstituted(string source)=>Assert.Null(SummonCommands.CopyableEncounter(source));
}
