using LiveInteract.Contracts;
using Xunit;
namespace LiveInteract.Tests;

/// <summary>双方共享容量与单只补位边界，不依赖Unity运行时。</summary>
[Collection("Monster pools")]
public sealed class SummonQueuePolicyTests
{
    [Theory]
    [InlineData(4,0,1,true,4,0,false)]
    [InlineData(4,4,1,false,4,0,false)]
    [InlineData(3,2,1,true,4,2,true)]
    [InlineData(3,2,1,false,4,2,false)]
    [InlineData(3,2,1,false,4,0,true)]
    [InlineData(100,100,1,false,0,0,true)]
    [InlineData(0,0,2,true,1,0,false)]
    public void BothFactionsShareTotalWithoutChangingEnemyOnlyLimit(int total,int enemies,int amount,bool friendly,int totalLimit,int enemyLimit,bool expected)
    {
        Assert.Equal(expected,SummonQueuePolicy.HasRoom(total,enemies,amount,friendly,totalLimit,enemyLimit));
    }

    [Fact]
    public void MultiSummonCanUseOneVacancyAtATimeWithoutMutatingParent()
    {
        using var approved=new ApprovedSummonsScope(new[]{"spawn.bone-goomba"});
        var parent=new ActionRequest{Id="gift-1",ActionId="summon.random.ally",Triggerer="观众",Arguments=new(){{"count","20"},{"power","4"}},DurationSeconds=5,JobId="job",UnitIndex=2,SourceType=LiveEventType.Gift};
        var first=SummonQueuePolicy.SingleUnit(parent,0);
        var retry=SummonQueuePolicy.SingleUnit(parent,0);
        var next=SummonQueuePolicy.SingleUnit(parent,1);
        Assert.Equal("1",first.Arguments["count"]);Assert.Equal("20",parent.Arguments["count"]);
        Assert.Equal(first.Id,retry.Id);Assert.NotEqual(first.Id,next.Id);
        Assert.Equal(parent.Id,first.SelectionSeed);Assert.Equal(0,first.SelectionOffset);Assert.Equal(1,next.SelectionOffset);
        Assert.Equal(parent.Triggerer,first.Triggerer);Assert.Equal(parent.DurationSeconds,first.DurationSeconds);
        Assert.Equal(parent.JobId,first.JobId);Assert.Equal(parent.UnitIndex,first.UnitIndex);
        Assert.Equal(parent.SourceType,first.SourceType);Assert.Equal("4",first.Arguments["power"]);
        Assert.True(SummonQueuePolicy.HasRoom(0,0,1,true,1,0));
        Assert.Empty(ActionValidation.Validate(first));
    }

    [Fact]
    public void SingleBossKeepsCatalogArgumentsAndFixedCount()
    {
        using var approved=new ApprovedSummonsScope(new[]{"spawn.coral-hunter"});
        var parent=new ActionRequest{Id="boss",ActionId="boss.random"};
        ActionValidation.FillDefaults(parent);
        var part=SummonQueuePolicy.SingleUnit(parent,0);
        Assert.Equal("1",part.Arguments["count"]);
        Assert.Equal(parent.Arguments.Keys.OrderBy(key=>key),part.Arguments.Keys.OrderBy(key=>key));
        Assert.Empty(ActionValidation.Validate(part));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QueueDoesNotRerollRandomMonstersOrLoseStrongPower(bool includeBoss)
    {
        using var approved=new ApprovedSummonsScope(new[]{"spawn.bone-goomba","spawn.slab-fly-large","spawn.coral-hunter"});
        var request=new ActionRequest{Id="stable-random",ActionId="summon.random",Arguments=new(){{"count","20"},{"power","4"},{"pool",includeBoss?"all":"ordinary"},{"faction","enemy"}}};
        for(var i=0;i<20;i++)
        {
            var part=SummonQueuePolicy.SingleUnit(request,i);
            Assert.Equal(NativeSummons.RandomMonster(request.Id+"-"+i,includeBoss).Id,NativeSummons.RandomMonster(part.SelectionSeed+"-"+part.SelectionOffset,includeBoss).Id);
            Assert.Equal("4",part.Arguments["power"]);Assert.Equal("enemy",part.Arguments["faction"]);
            Assert.Equal(request.Arguments["pool"],part.Arguments["pool"]);Assert.Equal("1",part.Arguments["count"]);
            Assert.Equal(NativeSummons.RandomMonster(request.Id+"-"+i,includeBoss).Id,NativeSummons.RandomMonster(part.SelectionSeed+"-"+part.SelectionOffset,part.Arguments["pool"]).Id);
            Assert.Empty(ActionValidation.Validate(part));
        }
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("all")]
    [InlineData("weak")]
    [InlineData("strongBoss")]
    public void QueuedRandomSummonRejectsEmptyPoolWithoutUnsafeFallback(string pool)
    {
        using var approved=new ApprovedSummonsScope(Array.Empty<string>());
        var parent=new ActionRequest{Id="closed-random",ActionId="summon.random",Arguments=new(){{"count","20"},{"power","4"},{"pool",pool},{"faction","enemy"}}};
        var part=SummonQueuePolicy.SingleUnit(parent,3);
        Assert.Equal(pool,part.Arguments["pool"]);Assert.Equal("4",part.Arguments["power"]);
        Assert.Equal("1",part.Arguments["count"]);Assert.Equal("20",parent.Arguments["count"]);
        Assert.Contains(ActionValidation.Validate(part),error=>error.Contains("批准")&&error.Contains(pool));
        var failure=Assert.Throws<InvalidOperationException>(()=>NativeSummons.RandomMonster(part.SelectionSeed+"-"+part.SelectionOffset,pool));
        Assert.Contains("批准",failure.Message);Assert.Contains(pool,failure.Message);
    }

    [Fact]
    public void QueuedBossRejectsEmptyPoolWithoutChangingArgumentsOrFallback()
    {
        using var approved=new ApprovedSummonsScope(Array.Empty<string>());
        var parent=new ActionRequest{Id="closed-boss",ActionId="boss.random"};
        ActionValidation.FillDefaults(parent);
        var part=SummonQueuePolicy.SingleUnit(parent,0);
        Assert.Equal("1",part.Arguments["count"]);
        Assert.Equal(parent.Arguments.Keys.OrderBy(key=>key),part.Arguments.Keys.OrderBy(key=>key));
        Assert.Contains(ActionValidation.Validate(part),error=>error.Contains("Boss池")&&error.Contains("批准"));
        Assert.Contains("批准",Assert.Throws<InvalidOperationException>(()=>NativeSummons.Boss(part.SelectionSeed+"-"+part.SelectionOffset)).Message);
    }
}
