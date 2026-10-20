using LiveInteract.Contracts;

namespace LiveInteract.Core;

/// <summary>规则命中结果，包含一次事件产生的触发数。</summary>
/// <param name="Rule">命中的规则快照。</param>
/// <param name="Count">需要执行的次数。</param>
public sealed record RuleMatch(RuleDefinition Rule, int Count);

/// <summary>持有有界会话计数、去重和冷却，不负责执行游戏动作。</summary>
public sealed class RuleEngine
{
    // 可测试的时间与随机源。
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<double> random;
    private readonly bool reliable;
    // 消息去重窗口，按入队顺序回收。
    private readonly Dictionary<string, DateTimeOffset> seen = new();
    private readonly Queue<string> seenOrder = new();
    // 规则与观众维度的会话计数。
    private readonly Dictionary<string, long> counters = new();
    private readonly Dictionary<string, DateTimeOffset> cooldowns = new();
    private readonly Dictionary<string, int> executions = new();

    /// <summary>建立规则求值器。</summary>
    /// <param name="clock">可测试的时钟。</param>
    /// <param name="random">可测试的随机源。</param>
    public RuleEngine(Func<DateTimeOffset>? clock = null, Func<double>? random = null, bool reliable = false)
    {
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.random = random ?? Random.Shared.NextDouble;
        this.reliable = reliable;
    }

    /// <summary>求值当前事件并返回命中规则；调用方按单一事件流串行调用。</summary>
    /// <param name="value">已归一化事件。</param>
    /// <param name="rules">按界面顺序排列的规则。</param>
    /// <returns>需要调度的规则与次数。</returns>
    public IReadOnlyList<RuleMatch> Evaluate(LiveEvent value, IEnumerable<RuleDefinition> rules)
    {
        var now = clock(); // 当前求值时间。
        var key = value.RoomId + ":" + value.Id; // 直播间隔离后的消息键。
        if (string.IsNullOrWhiteSpace(value.Id) || seen.ContainsKey(key))
        {
            return [];
        }

        // 保留十分钟或至多两万条消息，防止长时间直播无限增长。
        seen[key] = now;
        seenOrder.Enqueue(key);
        while (seenOrder.Count > 20000 || seenOrder.Count > 0 && now - seen[seenOrder.Peek()] > TimeSpan.FromMinutes(10))
        {
            seen.Remove(seenOrder.Dequeue());
        }
        var result = new List<RuleMatch>(); // 当前事件的求值结果。
        foreach (var rule in rules)
        {
            var trigger = rule.Trigger; // 当前触发条件。
            if (!rule.Enabled || rule.GameId != "silksong" || !Matches(value, trigger) || rule.Probability <= 0)
            {
                continue;
            }
            var userKey = rule.Id + ":user:" + value.UserId; // 单用户维度。
            if (!reliable && (IsCooling(rule.Id, rule.CooldownSeconds, now) ||
                rule.UserCooldownSeconds > 0 && (value.UserId.Length == 0 || IsCooling(userKey, rule.UserCooldownSeconds, now))))
            {
                continue;
            }

            // 冷却期间的事件不积压；通过筛选后再累计数量。
            var amount = value.Type is LiveEventType.Like or LiveEventType.Gift ? Math.Max(0, value.Count) : 1; // 实际增量。
            if (trigger.Counting == TriggerCounting.ComboEnd)
            {
                if (!value.ComboEnded)
                {
                    continue;
                }
                amount = value.ComboTotal;
            }
            var counterKey = rule.Id + ":count:" + value.RoomId; // 房间累计维度。
            if (trigger.Counting == TriggerCounting.PerUser)
            {
                if (value.UserId.Length == 0)
                {
                    continue;
                }
                counterKey += ":" + value.UserId;
            }
            var threshold = Math.Max(1, trigger.Threshold); // 有效门槛。
            var total = amount; // 当前可结算数量。
            if (trigger.Counting is TriggerCounting.RoomTotal or TriggerCounting.PerUser)
            {
                total = Math.Min(long.MaxValue / 2, counters.GetValueOrDefault(counterKey)) + Math.Min(long.MaxValue / 2, amount);
                counters[counterKey] = total % threshold;
            }
            var candidates = (int)Math.Min(reliable ? int.MaxValue : Math.Clamp(rule.MaximumPerEvent, 1, 1000), total / threshold); // 有界触发数。
            if (trigger.SingleExecution) { candidates = Math.Min(1, candidates); }
            var fired = 0; // 概率和上限处理后的触发数。
            if (rule.Probability >= 1 && (reliable || rule.CooldownSeconds == 0 && rule.UserCooldownSeconds == 0))
            {
                fired = rule.MaximumTriggers > 0 ? Math.Min(candidates, Math.Max(0, rule.MaximumTriggers - executions.GetValueOrDefault(rule.Id))) : candidates;
                executions[rule.Id] = (int)Math.Min(int.MaxValue, (long)executions.GetValueOrDefault(rule.Id) + fired);
            }
            else for (var index = 0; index < candidates; index++)
            {
                if (rule.MaximumTriggers > 0 && executions.GetValueOrDefault(rule.Id) >= rule.MaximumTriggers)
                {
                    break;
                }
                if (random() >= Math.Clamp(rule.Probability, 0, 1))
                {
                    continue;
                }
                executions[rule.Id] = executions.GetValueOrDefault(rule.Id) + 1;
                fired++;
                if (!reliable && (rule.CooldownSeconds > 0 || rule.UserCooldownSeconds > 0))
                {
                    break;
                }
            }
            if (fired == 0)
            {
                continue;
            }
            cooldowns[rule.Id] = now;
            cooldowns[userKey] = now;
            result.Add(new(rule, fired));
            if (rule.StopAfterMatch)
            {
                break;
            }
        }

        // 用户筛选计数有上界；到达上界时回收会话统计并明确由界面重置会话。
        if (counters.Count > 50000)
        {
            throw new InvalidOperationException("单用户累计维度已超过会话上限，请暂停并重置会话；未清空已有计数。");
        }
        if (cooldowns.Count > 50000)
        {
            foreach (var expired in cooldowns.Where(p => now - p.Value > TimeSpan.FromDays(1)).Select(p => p.Key).ToArray())
            {
                cooldowns.Remove(expired);
            }
        }
        return result;
    }

    /// <summary>每条未触发输入也给出可解释原因，不把没配规则误当作网络丢失。</summary>
    public string ExplainNoMatch(LiveEvent input, IEnumerable<RuleDefinition> rules)
    {
        var sameType = rules.Where(rule => rule.Enabled && rule.Trigger.Type == input.Type).ToArray();
        if (sameType.Length == 0) { return "尚未启用此来源的规则，输入已读取并保留"; }
        var matching = sameType.Where(rule => Matches(input, rule.Trigger)).ToArray();
        if (matching.Length == 0) { return input.Type == LiveEventType.Gift ? "礼物名称 / ID 或身份条件不匹配；实际收到：" + input.GiftName + "（ID " + input.GiftId + "）" : "关键词或身份条件未匹配，输入已保留"; }
        foreach (var rule in matching)
        {
            if (rule.MaximumTriggers > 0 && TriggeredCount(rule.Id) >= rule.MaximumTriggers) { return "达到主播设置的触发次数上限：" + rule.Name; }
            if (rule.Trigger.Counting == TriggerCounting.ComboEnd && !input.ComboEnded) { return "等待平台明确的连击结束消息"; }
            if (rule.Probability < 1) { return "未通过主播设置的概率条件：" + rule.Name; }
            if (input.Count == 0) { return "已读取本条消息，本次没有确认到新增数量"; }
            if (rule.Trigger.Threshold > 1) { return "正在累计：" + rule.Name + " " + AccumulatedCount(rule.Id) + " / " + rule.Trigger.Threshold; }
        }
        return "本条输入未满足当前计数 / 身份条件，已保留记录";
    }

    /// <summary>和事件决策一起保存累计状态，重启恢复不会重新计算已提交的单位。</summary>
    public string SaveState() => Newtonsoft.Json.JsonConvert.SerializeObject(new { Counters = counters, Cooldowns = cooldowns, Executions = executions });

    /// <summary>恢复最近提交的规则状态；重复消息由持久账本去重。</summary>
    public void LoadState(string json)
    {
        seen.Clear(); seenOrder.Clear(); counters.Clear(); cooldowns.Clear(); executions.Clear();
        if (json.Length == 0) { return; }
        var state = Newtonsoft.Json.Linq.JObject.Parse(json);
        foreach (var pair in state["Counters"]!.ToObject<Dictionary<string, long>>()!) { counters[pair.Key] = pair.Value; }
        foreach (var pair in state["Cooldowns"]!.ToObject<Dictionary<string, DateTimeOffset>>()!) { cooldowns[pair.Key] = pair.Value; }
        foreach (var pair in state["Executions"]!.ToObject<Dictionary<string, int>>()!) { executions[pair.Key] = pair.Value; }
    }

    /// <summary>读取规则在本会话已触发次数。</summary>
    /// <param name="ruleId">规则标识。</param>
    /// <returns>实际命中次数。</returns>
    public int TriggeredCount(string ruleId) => executions.GetValueOrDefault(ruleId);

    /// <summary>查询规则当前累计余数。</summary>
    /// <param name="ruleId">规则标识。</param>
    /// <returns>该规则各计数维度的累计余数之和。</returns>
    public long AccumulatedCount(string ruleId) => counters.Where(p => p.Key.StartsWith(ruleId + ":count:", StringComparison.Ordinal)).Sum(p => p.Value);

    /// <summary>判断指定维度是否仍在冷却中。</summary>
    /// <param name="key">冷却键。</param>
    /// <param name="seconds">冷却时间。</param>
    /// <param name="now">当前时间。</param>
    /// <returns>未到期返回真。</returns>
    private bool IsCooling(string key, double seconds, DateTimeOffset now) => seconds > 0 && cooldowns.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(seconds);

    /// <summary>匹配可获取的真实输入字段。</summary>
    /// <param name="value">输入事件。</param>
    /// <param name="trigger">筛选条件。</param>
    /// <returns>所有条件都满足时返回真。</returns>
    private static bool Matches(LiveEvent value, TriggerDefinition trigger)
    {
        if (value.Type != trigger.Type || trigger.BlockedUsers.Contains(value.UserId) ||
            trigger.AllowedUsers.Count > 0 && !trigger.AllowedUsers.Contains(value.UserId) ||
            trigger.MinimumFanLevel > 0 && (value.FanLevel == null || value.FanLevel < trigger.MinimumFanLevel) ||
            trigger.RequireFollower && value.IsFollower != true)
        {
            return false;
        }
        if (value.Type == LiveEventType.Gift && (trigger.GiftId.Length > 0 ? trigger.GiftId != value.GiftId : trigger.GiftName.Length > 0 && trigger.GiftName != value.GiftName && !trigger.GiftNameAliases.Contains(value.GiftName)))
        {
            return false;
        }
        if (value.Type == LiveEventType.Gift && trigger.FreeOrUnknownGiftOnly && value.GiftUnitValue > 0) { return false; }
        if (value.Type == LiveEventType.Gift && (value.GiftUnitValue < trigger.MinGiftUnitValue || trigger.MaxGiftUnitValue > 0 && value.GiftUnitValue > trigger.MaxGiftUnitValue)) { return false; }
        if (trigger.Keywords.Count == 0)
        {
            return true;
        }
        return trigger.Keywords.Where(k => !string.IsNullOrWhiteSpace(k)).Any(k => trigger.TextMatching switch
        {
            TextMatching.Exact => string.Equals(value.Text, k, StringComparison.OrdinalIgnoreCase),
            TextMatching.StartsWith => value.Text.StartsWith(k, StringComparison.OrdinalIgnoreCase),
            _ => value.Text.Contains(k, StringComparison.OrdinalIgnoreCase)
        });
    }
}
