namespace LiveInteract.Contracts;

/// <summary>按原作抛射重力计算友军的出手横向速度；不追踪已离手的弹体。</summary>
public static class CompanionAim
{
    /// <summary>只校准已核对的原生上抛弹，不接管其他单位的散射或移动。</summary>
    /// <param name="asset">召唤目录标识。</param>
    /// <returns>是否使用目标身体落点校准。</returns>
    public static bool UsesTargetedArc(string? asset) => asset is "spawn.pilgrim-bell-thrower" or "spawn.pilgrim-moss-spitter";

    /// <summary>返回下降途中到达目标高度所需的横向速度，无可达落点时保持原作。</summary>
    /// <param name="deltaX">目标与出手点的横向距离。</param>
    /// <param name="deltaY">目标与出手点的高度差。</param>
    /// <param name="verticalSpeed">原作垂直起速。</param>
    /// <param name="gravity">原作实际向下重力加速度。</param>
    /// <returns>横向速度，无法计算时为 null。</returns>
    public static double? HorizontalSpeed(double deltaX,double deltaY,double verticalSpeed,double gravity)
    {
        // 选择下降段交点，保留原作上抛、重力和落地反弹。
        if(gravity>=0)return null;
        var discriminant=verticalSpeed*verticalSpeed+2*gravity*deltaY;
        if(discriminant<0)return null;
        var time=(-verticalSpeed-System.Math.Sqrt(discriminant))/gravity;
        return time>0?deltaX/time:null;
    }
}
