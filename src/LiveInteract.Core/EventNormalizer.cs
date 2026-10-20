using LiveInteract.Contracts;

namespace LiveInteract.Core;

/// <summary>将礼物连击累计数量转换为增量，保留结束结算所需总数。</summary>
public sealed class EventNormalizer
{
    // 消息和连击状态使用有界窗口。
    private readonly HashSet<string> seen = new();
    private readonly Queue<string> seenOrder = new();
    private readonly Dictionary<string, (long Count, DateTimeOffset At, bool Ended)> combos = new();
    private DateTimeOffset lastCleanup;

    /// <summary>归一化事件；不修改原始输入对象。</summary>
    /// <param name="value">原始输入。</param>
    /// <returns>增量事件，重复或无新增且未结束的输入返回空。</returns>
    public LiveEvent? Normalize(LiveEvent value)
    {
        var messageKey = value.RoomId + ":" + value.Id; // 消息去重标识。
        if (string.IsNullOrWhiteSpace(value.Id) || !seen.Add(messageKey))
        {
            return null;
        }
        seenOrder.Enqueue(messageKey);
        while (seenOrder.Count > 20000)
        {
            seen.Remove(seenOrder.Dequeue());
        }
        var output = Newtonsoft.Json.JsonConvert.DeserializeObject<LiveEvent>(Newtonsoft.Json.JsonConvert.SerializeObject(value))!; // 保留原始数据用于诊断。
        if (value.Type != LiveEventType.Gift || !value.IsCumulative || value.ComboId.Length == 0)
        {
            output.Count = Math.Max(0, value.Count);
            output.ComboTotal = Math.Max(output.ComboTotal, output.Count);
            return output;
        }

        // 由组 ID 区分礼物连击；结束包只结算总量，不重复产生增量。
        var comboKey = value.RoomId + ":" + value.UserId + ":" + value.GiftId + ":" + value.ComboId; // 连击键。
        var previous = combos.GetValueOrDefault(comboKey); // 已确认累计值。
        if (previous.Ended && value.Count <= previous.Count) { return null; }
        output.ComboEnded = value.ComboEnded && !previous.Ended;
        var total = Math.Max(previous.Count, Math.Max(0, value.Count)); // 单组内累计不回退。
        output.Count = Math.Max(0, total - previous.Count);
        output.ComboTotal = total;
        combos[comboKey] = (total, value.ReceivedAt, previous.Ended || value.ComboEnded);
        if (combos.Count > 1000 && value.ReceivedAt - lastCleanup > TimeSpan.FromSeconds(30))
        {
            lastCleanup = value.ReceivedAt;
            foreach (var expired in combos.Where(p => value.ReceivedAt - p.Value.At > TimeSpan.FromMinutes(10)).Select(p => p.Key).ToArray())
            {
                combos.Remove(expired);
            }
            if (combos.Count > 100000)
            {
                throw new InvalidOperationException("连击状态超过上限，请暂停并重新连接直播间。");
            }
        }
        return output.Count == 0 && !output.ComboEnded ? null : output;
    }
}
