using LiveInteract.Contracts;
using LiveInteract.Core;
using Xunit;
namespace LiveInteract.Tests;
public sealed class GiftDisplayTests
{
 [Fact] public void TierIsVisibleInRuleList()=>Assert.Contains("100–499钻",RuleDisplay.Trigger(new(){Type=LiveEventType.Gift,MinGiftUnitValue=100,MaxGiftUnitValue=499}));
 [Fact] public void PersistentCompanionLifetimeIsVisible()=>Assert.Contains("持续跟随",RuleDisplay.Effect(new(){ActionId="ally.clockwork",Arguments=new(){["count"]="1",["lifetime"]="0"}}));
}
