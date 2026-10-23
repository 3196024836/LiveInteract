namespace LiveInteract.Contracts;

/// <summary>友军墙体探测保留真实地形，避免把作为攻击目标的敌人误当成墙。</summary>
public static class CompanionTerrainProbe
{
    /// <summary>只调整已确认的非反选 WALL 射线；调用方保留原作 AI 的其他探测。</summary>
    /// <param name="mask">原作转换后的层掩码。</param>
    /// <param name="terrainLayer">游戏地形层。</param>
    /// <param name="enemyLayer">游戏敌人身体层。</param>
    /// <param name="friendly">是否为改编友军执行，不含原生机械小弟。</param>
    /// <param name="eventName">命中后发送的原作事件。</param>
    /// <returns>本次调用使用的层掩码。</returns>
    public static int FilterMask(int mask, int terrainLayer, int enemyLayer, bool friendly, string eventName)
        => friendly && eventName == "WALL" && (mask & (1 << terrainLayer)) != 0
            ? mask & ~(1 << enemyLayer) : mask;
    /// <summary>仅识别最终之爪原作Charge的END墙体探测，不扩大到其他招式。</summary>
    public static bool UsesSolidChargeProbe(string asset, string fsm, string state, string hitEvent)
        => asset == "spawn.bone-hunter-chief" && fsm == "Control" && state == "Charge" && hitEvent == "END";

    /// <summary>只有五厘米内的实体地形侧面接触可补足原中心射线。</summary>
    public static bool IsSolidWallImpact(bool terrain, bool trigger, double normalDotDirection, double distance)
        => terrain && !trigger && normalDotDirection < -.5 && distance >= 0 && distance <= .05f;
}
