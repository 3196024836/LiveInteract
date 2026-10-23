namespace LiveInteract.Contracts;
/// <summary>随从停走、屏外召回与索敌租期；不接管原作攻击动作。</summary>
public static class CompanionFollowPolicy
{
    /// <summary>行走配置优先；停止中的地面爬行组件仍保留原作贴图方向，不能退回通用朝向。</summary>
    public static bool SpriteFacesLeft(bool? walkFacesLeft, bool? floorCrawlerFacesRight, bool fallbackFacesRight)
        => walkFacesLeft ?? (floorCrawlerFacesRight.HasValue ? !floorCrawlerFacesRight.Value : !fallbackFacesRight);
    public static bool CanWalkUpStep(double rise,bool grounded,bool headroom)=>rise>=-.03&&rise<=.45&&grounded&&headroom;
    public static bool ShouldFollow(double distance,bool moving)=>distance>(moving?2.5:3.5);
    public static bool CanRecall(bool visible,double offscreenSeconds)=>!visible&&offscreenSeconds>=.75;
    public static bool CanSteer(bool hasEnemy,bool nativeFriendly,bool safeLocomotion)=>!hasEnemy&&!nativeFriendly&&safeLocomotion;
    public static bool KeepTarget(bool alive,bool friendly,bool hero,double actorDistance,double heroDistance)=>alive&&!friendly&&!hero&&actorDistance<=18&&heroDistance<=22;
    /// <summary>只判断来源场景约束是否仍可作用，根召唤物与装饰子部件分开处理。</summary>
    /// <param name="ownedRoot">约束是否直接位于根召唤物。</param>
    /// <param name="localSpace">原作是否为局部约束；根节点换父级后，局部约束同样属于旧场景。</param>
    /// <returns>是否继续运行原作位置约束。</returns>
    public static bool CanApplySourcePositionConstraint(bool ownedRoot, bool localSpace)
        => !ownedRoot;
}
