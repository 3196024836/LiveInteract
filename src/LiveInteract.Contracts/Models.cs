using System;
using System.Collections.Generic;

namespace LiveInteract.Contracts;

/// <summary>直播输入的实际事件类型，不以礼物名称推断类型。</summary>
public enum LiveEventType { Chat, Gift, Like, Follow, Enter, Share, FanClub }

/// <summary>无脚本的数值联动来源。</summary>
public enum ParameterValueSource { EventCount, ComboTotal, TriggerIndex }

/// <summary>提供参数编辑控件所需的数据类型。</summary>
public enum ParameterKind { Number, Integer, Boolean, Choice, Text }

/// <summary>描述无脚本动作树的执行方式。</summary>
public enum StepKind { Action, Sequence, Parallel, Delay, Repeat, Random }

/// <summary>同类临时效果重入时的组合方式。</summary>
public enum EffectStacking { Refresh, Extend, Stack }

/// <summary>描述事件计数的累计边界。</summary>
public enum TriggerCounting { PerEvent, RoomTotal, PerUser, ComboEnd }

/// <summary>描述弹幕文本的匹配方式。</summary>
public enum TextMatching { Contains, Exact, StartsWith }

/// <summary>统一直播事件；缺失的身份信息保持未知，不补造数据。</summary>
public sealed class LiveEvent
{
    /// <summary>平台消息标识，模拟事件使用独立前缀。</summary>
    public string Id { get; set; } = "";
    /// <summary>直播间或来源标识。</summary>
    public string RoomId { get; set; } = "";
    /// <summary>事件类型。</summary>
    public LiveEventType Type { get; set; }
    /// <summary>实际用户标识；缺失时为空。</summary>
    public string UserId { get; set; } = "";
    /// <summary>显示昵称。</summary>
    public string Nickname { get; set; } = "观众";
    /// <summary>弹幕或事件文本。</summary>
    public string Text { get; set; } = "";
    /// <summary>礼物标识。</summary>
    public string GiftId { get; set; } = "";
    /// <summary>礼物名称。</summary>
    public string GiftName { get; set; } = "";
    /// <summary>平台明确提供的礼物单价（钻），未知为0；不由名称猜测。</summary>
    public long GiftUnitValue { get; set; }
    /// <summary>当前新增数量，归一化前可为连击累计值。</summary>
    public long Count { get; set; } = 1;
    /// <summary>连击总量。</summary>
    public long ComboTotal { get; set; }
    /// <summary>连击分组标识。</summary>
    public string ComboId { get; set; } = "";
    /// <summary>输入数量是否为连击累计值。</summary>
    public bool IsCumulative { get; set; }
    /// <summary>平台是否明确标记连击结束。</summary>
    public bool ComboEnded { get; set; }
    /// <summary>已获取的粉丝等级。</summary>
    public int? FanLevel { get; set; }
    /// <summary>已获取的关注关系。</summary>
    public bool? IsFollower { get; set; }
    /// <summary>控制台接收时间。</summary>
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>平台明确提供的事件发生时间；没有时保持空。</summary>
    public DateTimeOffset? OccurredAt { get; set; }
    /// <summary>平台数据不完整，需要核对而不能静默丢弃。</summary>
    public bool NeedsReview { get; set; }
    /// <summary>需要核对的具体原因。</summary>
    public string ReviewReason { get; set; } = "";
    /// <summary>是否由本地模拟器产生。</summary>
    public bool IsSimulation { get; set; }
}

/// <summary>一条规则的输入筛选、门槛和累计范围。</summary>
public sealed class TriggerDefinition
{
    /// <summary>匹配事件类型。</summary>
    public LiveEventType Type { get; set; } = LiveEventType.Chat;
    /// <summary>任一关键词匹配即可，空列表匹配所有同类事件。</summary>
    public List<string> Keywords { get; set; } = new();
    /// <summary>文本匹配方式。</summary>
    public TextMatching TextMatching { get; set; } = TextMatching.Contains;
    /// <summary>优先使用礼物 ID 匹配。</summary>
    public string GiftId { get; set; } = "";
    /// <summary>未指定 ID 时可使用礼物名称。</summary>
    public string GiftName { get; set; } = "";
    /// <summary>礼物最低单价，0不限制。</summary>
    public long MinGiftUnitValue { get; set; }
    /// <summary>礼物最高单价，0不限制。</summary>
    public long MaxGiftUnitValue { get; set; }
    /// <summary>仅匹配零价或单价未知的礼物；用于无敌怪的基础助力，旧配置默认关闭。</summary>
    public bool FreeOrUnknownGiftOnly { get; set; }
    /// <summary>同一种礼物的明确名称别名；不模糊匹配。</summary>
    public List<string> GiftNameAliases { get; set; } = new();
    /// <summary>允许用户列表；非空时未知用户不匹配。</summary>
    public List<string> AllowedUsers { get; set; } = new();
    /// <summary>拒绝用户列表。</summary>
    public List<string> BlockedUsers { get; set; } = new();
    /// <summary>最低粉丝等级；零表示不要求。</summary>
    public int MinimumFanLevel { get; set; }
    /// <summary>是否要求已知的关注关系。</summary>
    public bool RequireFollower { get; set; }
    /// <summary>计数范围。</summary>
    public TriggerCounting Counting { get; set; }
    /// <summary>达到门槛后只执行一组，适合将数量映射到动作参数。</summary>
    public bool SingleExecution { get; set; }
    /// <summary>每达到多少单位触发一次。</summary>
    public int Threshold { get; set; } = 1;
}

/// <summary>无脚本的递归动作树，允许顺序、并行、等待、重复与加权随机。</summary>
public sealed class ActionStep
{
    /// <summary>执行方式。</summary>
    public StepKind Kind { get; set; }
    /// <summary>动作目录标识。</summary>
    public string ActionId { get; set; } = "health.add";
    /// <summary>参数字符串由动作目录负责验证和转换。</summary>
    public Dictionary<string, string> Arguments { get; set; } = new();
    /// <summary>可选的数值联动，不接受脚本或任意表达式。</summary>
    public Dictionary<string, NumericBinding> ParameterBindings { get; set; } = new();
    /// <summary>临时效果持续时间。</summary>
    public double DurationSeconds { get; set; } = 10;
    /// <summary>重复次数。</summary>
    public int RepeatCount { get; set; } = 1;
    /// <summary>延迟秒数。</summary>
    public double DelaySeconds { get; set; }
    /// <summary>随机节点中当前分支的权重。</summary>
    public double Weight { get; set; } = 1;
    /// <summary>同类效果处理策略。</summary>
    public EffectStacking Stacking { get; set; }
    /// <summary>容器的子动作。</summary>
    public List<ActionStep> Children { get; set; } = new();
}

/// <summary>可导入导出的完整规则，不携带账号或本机路径。</summary>
public sealed class RuleDefinition
{
    /// <summary>稳定规则标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>规则名称。</summary>
    public string Name { get; set; } = "新规则";
    /// <summary>明确开启后才执行。</summary>
    public bool Enabled { get; set; }
    /// <summary>所属游戏。</summary>
    public string GameId { get; set; } = "silksong";
    /// <summary>输入条件。</summary>
    public TriggerDefinition Trigger { get; set; } = new();
    /// <summary>概率范围零到一。</summary>
    public double Probability { get; set; } = 1;
    /// <summary>全局冷却秒数。</summary>
    public double CooldownSeconds { get; set; }
    /// <summary>单用户冷却秒数。</summary>
    public double UserCooldownSeconds { get; set; }
    /// <summary>本次会话最多触发次数；零表示不限。</summary>
    public int MaximumTriggers { get; set; }
    /// <summary>一个事件最多生成的触发数。</summary>
    public int MaximumPerEvent { get; set; } = 20;
    /// <summary>匹配后停止继续匹配后续规则。</summary>
    public bool StopAfterMatch { get; set; }
    /// <summary>执行根节点。</summary>
    public ActionStep Root { get; set; } = new();
}

/// <summary>便携的规则文档，包含版本而不包含登录态。</summary>
public sealed class RuleProfile
{
    /// <summary>文档版本。</summary>
    public int SchemaVersion { get; set; } = 1;
    /// <summary>配置名称。</summary>
    public string Name { get; set; } = "我的直播配置";
    /// <summary>可选的建议同时生成上限；0不设总上限，空值保留当前设置。</summary>
    public int? SpawnLimitOverride { get; set; }
    /// <summary>规则集合。</summary>
    public List<RuleDefinition> Rules { get; set; } = new();
}

/// <summary>动态生成参数控件和校验输入的描述。</summary>
public sealed class ActionParameter
{
    /// <summary>参数键。</summary>
    public string Key { get; set; } = "value";
    /// <summary>中文标签。</summary>
    public string Label { get; set; } = "数值";
    /// <summary>控件类型。</summary>
    public ParameterKind Kind { get; set; }
    /// <summary>默认输入。</summary>
    public string DefaultValue { get; set; } = "1";
    /// <summary>最小数值。</summary>
    public double Minimum { get; set; } = -100000;
    /// <summary>最大数值。</summary>
    public double Maximum { get; set; } = 100000;
    /// <summary>选择项的值及显示名。</summary>
    public Dictionary<string, string> Choices { get; set; } = new();
}

/// <summary>跨游戏的动作目录条目，负责说明参数、执行条件和存档风险。</summary>
public sealed class ActionDescriptor
{
    /// <summary>稳定动作标识。</summary>
    public string Id { get; set; } = "";
    /// <summary>中文名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>仅用于查找的分类。</summary>
    public string Category { get; set; } = "";
    /// <summary>行为说明和限制。</summary>
    public string Description { get; set; } = "";
    /// <summary>是否可能永久改变存档。</summary>
    public bool IsPersistent { get; set; }
    /// <summary>是否使用持续时间。</summary>
    public bool IsTemporary { get; set; }
    /// <summary>是否需要角色处于可操作的游戏场景。</summary>
    public bool RequiresGameplay { get; set; } = true;
    /// <summary>当前支持情况：implemented 或 unavailable。</summary>
    public string Support { get; set; } = "implemented";
    /// <summary>数据驱动的参数集合。</summary>
    public List<ActionParameter> Parameters { get; set; } = new();
}

/// <summary>通过本机管道发送的一次游戏指令。</summary>
public sealed class ActionRequest
{
    /// <summary>唯一指令标识，用于防止断线或超时重复执行。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>目标游戏。</summary>
    public string GameId { get; set; } = "silksong";
    /// <summary>动作目录标识。</summary>
    public string ActionId { get; set; } = "";
    /// <summary>动作参数。</summary>
    public Dictionary<string, string> Arguments { get; set; } = new();
    /// <summary>效果时间。</summary>
    public double DurationSeconds { get; set; }
    /// <summary>叠加策略。</summary>
    public EffectStacking Stacking { get; set; }
    /// <summary>本次会话是否已由主播确认永久修改。</summary>
    public bool AllowPersistent { get; set; }
    /// <summary>游戏内显示的触发说明，不含用户凭据。</summary>
    public string DisplayText { get; set; } = "";
    /// <summary>持久队列任务和单位序号，用于结果核对。</summary>
    public string JobId { get; set; } = "";
    public long UnitIndex { get; set; }
    /// <summary>平台实际提供的触发者昵称，不从规则名拆解。</summary>
    public string Triggerer { get; set; } = "";
    /// <summary>简洁的效果与实际参数。</summary>
    public string EffectText { get; set; } = "";
    /// <summary>真实输入来源，供控制台筛选结果。</summary>
    public LiveEventType? SourceType { get; set; }
}

/// <summary>游戏执行后的回执，不把排队当作成功。</summary>
public sealed class ActionResult
{
    /// <summary>对应指令标识。</summary>
    public string RequestId { get; set; } = "";
    /// <summary>是否已经执行成功。</summary>
    public bool Success { get; set; }
    /// <summary>主播可读的结果或失败原因。</summary>
    public string Message { get; set; } = "";
    /// <summary>可分类的失败原因；UNKNOWN 表示结果未确认，NO_CHANGE 表示合法但无变化。</summary>
    public string ErrorCode { get; set; } = "";
    /// <summary>仅在确定尚未修改游戏时允许等待后重试。</summary>
    public bool Retryable { get; set; }
    /// <summary>执行耗时。</summary>
    public double ElapsedMilliseconds { get; set; }
}

/// <summary>游戏端定期报告的真实连接状态。</summary>
public sealed class GameStatus
{
    /// <summary>协议版本。</summary>
    public int ProtocolVersion { get; set; } = 1;
    /// <summary>游戏 ID。</summary>
    public string GameId { get; set; } = "silksong";
    /// <summary>游戏版本。</summary>
    public string GameVersion { get; set; } = "";
    /// <summary>专用 Mod 版本。</summary>
    public string ModVersion { get; set; } = "";
    /// <summary>实际场景名称。</summary>
    public string Scene { get; set; } = "";
    /// <summary>是否可以执行需要角色的动作。</summary>
    public bool CanAct { get; set; }
    /// <summary>当前生命。</summary>
    public int Health { get; set; }
    /// <summary>实际蓝色生命，用于核对即时生命奖励。</summary>
    public int BlueHealth { get; set; }
    public int ShieldPoints { get; set; }
    /// <summary>本工具当前在场的原生友军数量。</summary>
    public int CompanionCount { get; set; }
    /// <summary>仍可拾取的应援补给数量。</summary>
    public int PickupCount { get; set; }
    /// <summary>独立倒计时的外观整活层数。</summary>
    public int SizeLayerCount { get; set; }
    /// <summary>仅测试编译在隔离存档启用后提供证明。</summary>
    public string DiagnosticSession { get; set; } = "";
    /// <summary>当前丝线。</summary>
    public int Silk { get; set; }
    /// <summary>当前可用的敌人资源目录。</summary>
    public List<string> EnemyIds { get; set; } = new();
    /// <summary>当前实际加载的工具标识。</summary>
    public List<string> ToolIds { get; set; } = new();
    /// <summary>当前实际加载的纹章标识。</summary>
    public List<string> CrestIds { get; set; } = new();
    /// <summary>角色战斗状态机中的实际事件。</summary>
    public List<string> SkillEvents { get; set; } = new();
    /// <summary>可定位的场景资源标识。</summary>
    public List<string> SceneIds { get; set; } = new();
    /// <summary>候选敌人预制资源，仍需按场景实测。</summary>
    public List<string> EnemyAssetIds { get; set; } = new();
    /// <summary>已在游戏当前资源目录找到的具名怪物标识。</summary>
    public List<string> SpawnableMonsterIds { get; set; } = new();
    /// <summary>仍生效的临时效果。</summary>
    public List<string> ActiveEffects { get; set; } = new();
    /// <summary>已完成的游戏端自检。</summary>
    public List<string> Diagnostics { get; set; } = new();
}

/// <summary>本机 JSONL 协议信封，拒绝未知协议或游戏标识。</summary>
public sealed class BridgeMessage
{
    /// <summary>信封类型：status、action、result、overlay 或 reset。</summary>
    public string Kind { get; set; } = "";
    /// <summary>游戏指令。</summary>
    public ActionRequest? Request { get; set; }
    /// <summary>执行回执。</summary>
    public ActionResult? Result { get; set; }
    /// <summary>状态快照。</summary>
    public GameStatus? Status { get; set; }
    /// <summary>可选设置内容。</summary>
    public string Text { get; set; } = "";
}

/// <summary>一个数值参数与观众数量的线性映射，最终仍受动作边界校验。</summary>
public sealed class NumericBinding
{
    /// <summary>数量来源。</summary>
    public ParameterValueSource Source { get; set; }
    /// <summary>乘数。</summary>
    public double Multiplier { get; set; } = 1;
    /// <summary>加数。</summary>
    public double Offset { get; set; }
}

/// <summary>一次规则触发所需的数值上下文，不包含账号凭据。</summary>
public sealed class ActionContext
{
    /// <summary>持久保存原弹幕，供精准召唤解析；旧任务默认空。</summary>
    public string InputText { get; set; } = "";
    /// <summary>当前事件的新增数量。</summary>
    public long EventCount { get; set; }
    /// <summary>当前连击累计量。</summary>
    public long ComboTotal { get; set; }
    /// <summary>当前事件内的触发序号。</summary>
    public int TriggerIndex { get; set; } = 1;
    /// <summary>一次排队单位及其观众来源。</summary>
    public string JobId { get; set; } = "";
    public long UnitIndex { get; set; }
    public string Triggerer { get; set; } = "";
    public string RequestIdPrefix { get; set; } = "";
    public LiveEventType? SourceType { get; set; }
    private int commandIndex;
    /// <summary>为同一单位中的叶动作提供稳定且互不相同的标识。</summary>
    public string NextCommandId() => RequestIdPrefix.Length == 0 ? Guid.NewGuid().ToString("N") : RequestIdPrefix + "-" + System.Threading.Interlocked.Increment(ref commandIndex);
}
