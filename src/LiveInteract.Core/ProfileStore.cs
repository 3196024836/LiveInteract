using LiveInteract.Contracts;
using Newtonsoft.Json;

namespace LiveInteract.Core;

/// <summary>验证并原子保存不含账号和本机路径的规则文档。</summary>
public static class ProfileStore
{
    /// <summary>校验单个动作请求。</summary>
    /// <param name="request">准备执行的请求。</param>
    /// <returns>错误列表。</returns>
    public static IReadOnlyList<string> ValidateAction(ActionRequest request) => ActionValidation.Validate(request);

    /// <summary>读取配置；外部导入始终关闭全部规则。</summary>
    /// <param name="json">规则 JSON。</param>
    /// <param name="importing">是否外部导入。</param>
    /// <returns>可编辑配置。</returns>
    public static RuleProfile Read(string json, bool importing = true)
    {
        if (json.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("规则文件不能超过 4 MB。");
        }
        RuleProfile profile; // 通过受限反序列化后的文档。
        try
        {
            profile = JsonConvert.DeserializeObject<RuleProfile>(json, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.None, MaxDepth = 32, MissingMemberHandling = MissingMemberHandling.Error
            }) ?? throw new InvalidDataException("规则文件为空。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("规则文件格式无效：" + exception.Message, exception);
        }
        if (profile.SpawnLimitOverride is < 0 or > 10000) { throw new InvalidDataException("配置生成上限应为0至10000；0表示不设总上限。"); }
        if (profile.SchemaVersion != 1 || profile.Rules == null || profile.Rules.Count > 500)
        {
            throw new InvalidDataException("不支持的配置版本或规则数量，当前仅支持版本 1、最多 500 条规则。");
        }
        if (profile.Rules.Select(rule => rule.Id).Distinct().Count() != profile.Rules.Count)
        {
            throw new InvalidDataException("配置包含重复的规则 ID。");
        }
        foreach (var rule in profile.Rules)
        {
            var errors = ValidateRule(rule); // 结构性错误。
            if (errors.Count > 0)
            {
                throw new InvalidDataException(rule.Name + "：" + string.Join("；", errors));
            }
            if (importing || rule.GameId != "silksong")
            {
                rule.Enabled = false;
            }
        }
        return profile;
    }

    /// <summary>校验规则和有界动作树，编辑阶段允许配置尚未获许可的永久动作。</summary>
    /// <param name="rule">被编辑的规则。</param>
    /// <returns>错误列表。</returns>
    public static IReadOnlyList<string> ValidateRule(RuleDefinition rule)
    {
        var errors = new List<string>(); // 本次校验错误。
        if (string.IsNullOrWhiteSpace(rule.Id) || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 120 || rule.Trigger == null || rule.Root == null)
        {
            return ["规则必须有名称、标识、触发条件和动作。"];
        }
        if (!double.IsFinite(rule.Probability) || rule.Probability < 0 || rule.Probability > 1 ||
            !double.IsFinite(rule.CooldownSeconds) || rule.CooldownSeconds < 0 || rule.CooldownSeconds > 86400 ||
            !double.IsFinite(rule.UserCooldownSeconds) || rule.UserCooldownSeconds < 0 || rule.UserCooldownSeconds > 86400 ||
            rule.Trigger.Threshold < 1 || rule.Trigger.Threshold > 1000000000 || rule.MaximumPerEvent is < 1 or > 1000 || rule.MaximumTriggers < 0)
        {
            errors.Add("概率、冷却或触发次数超出有效范围。");
        }
        if (rule.Trigger.FreeOrUnknownGiftOnly && (rule.Root.ActionId.StartsWith("spawn.", StringComparison.Ordinal) || rule.Root.ActionId.StartsWith("boss.", StringComparison.Ordinal) || rule.Root.ActionId is "enemy.clone" or "enemy.cloneSafe")) { errors.Add("零价/未知礼物不能绑定敌怪、复制敌人或Boss，请改为助力效果。"); }
        if (rule.Trigger.FreeOrUnknownGiftOnly && (rule.Trigger.Type != LiveEventType.Gift || rule.Trigger.MinGiftUnitValue > 0)) { errors.Add("零价/未知礼物筛选不能同时要求付费或用于非礼物来源。"); }
        if (rule.Trigger.GiftNameAliases == null || rule.Trigger.GiftNameAliases.Count > 32 || rule.Trigger.GiftNameAliases.Any(n=>n==null || n.Length>128)) { errors.Add("礼物名称别名无效。"); }
        if (rule.Trigger.MinGiftUnitValue < 0 || rule.Trigger.MaxGiftUnitValue < 0 || rule.Trigger.MaxGiftUnitValue > 0 && rule.Trigger.MaxGiftUnitValue < rule.Trigger.MinGiftUnitValue) { errors.Add("礼物单价档位范围无效。"); }
        if (!Enum.IsDefined(rule.Trigger.Type) || !Enum.IsDefined(rule.Trigger.Counting) || !Enum.IsDefined(rule.Trigger.TextMatching) || rule.Trigger.MinimumFanLevel < 0 || rule.Trigger.MinimumFanLevel > 10000 || rule.Trigger.AllowedUsers == null || rule.Trigger.BlockedUsers == null || rule.Trigger.Keywords == null) { errors.Add("触发类型或身份筛选格式无效。"); }
        var count = 0; // 限制图形配置产生的动作树大小。
        Walk(rule.Root, 0);
        return errors;

        // 逐节点校验，避免导入文件构造无限或过深执行图。
        void Walk(ActionStep step, int depth)
        {
            if (++count > 200 || depth > 12 || step.Children == null)
            {
                errors.Add("动作树不能超过 200 节点或 12 层。");
                return;
            }
            if (!Enum.IsDefined(step.Kind) || !double.IsFinite(step.DelaySeconds) || step.DelaySeconds < 0 || step.DelaySeconds > 600 ||
                step.RepeatCount < 1 || step.RepeatCount > 1000 || !double.IsFinite(step.Weight) || step.Weight < 0)
            {
                errors.Add("动作节点类型、延时、重复次数或权重无效。");
            }
            if (step.Kind == StepKind.Action && rule.GameId == "silksong")
            {
                if (step.Arguments == null || step.ParameterBindings == null) { errors.Add("动作参数缺失。"); return; }
                var arguments = new Dictionary<string, string>(step.Arguments); // 联动字段只验证来源，执行时再解析数值。
                foreach (var pair in step.ParameterBindings)
                {
                    var parameter = ActionCatalog.All.FirstOrDefault(item => item.Id == step.ActionId)?.Parameters.FirstOrDefault(item => item.Key == pair.Key); // 目标参数。
                    if (parameter == null || parameter.Kind is not (ParameterKind.Number or ParameterKind.Integer) || pair.Value == null || !Enum.IsDefined(pair.Value.Source) || !double.IsFinite(pair.Value.Multiplier) || !double.IsFinite(pair.Value.Offset)) { errors.Add("无效的数量联动配置。"); }
                    else { arguments[pair.Key] = parameter.DefaultValue; }
                }
                errors.AddRange(ActionValidation.Validate(new() { ActionId = step.ActionId, Arguments = arguments, DurationSeconds = step.DurationSeconds, Stacking = step.Stacking, AllowPersistent = true }));
            }
            if (step.Kind is StepKind.Sequence or StepKind.Parallel or StepKind.Repeat or StepKind.Random && step.Children.Count == 0)
            {
                errors.Add("组合节点至少需要一个子动作。");
            }
            foreach (var child in step.Children.Take(201))
            {
                if (child == null)
                {
                    errors.Add("动作树包含空节点。");
                }
                else
                {
                    Walk(child, depth + 1);
                }
            }
        }
    }

    /// <summary>通过同目录临时文件原子替换配置。</summary>
    /// <param name="file">保存路径。</param>
    /// <param name="profile">已编辑的规则文档。</param>
    public static void Save(string file, RuleProfile profile)
    {
        var json = JsonConvert.SerializeObject(profile, Formatting.Indented); // 先生成并复查文档。
        Read(json, false);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + ".tmp"; // 同目录原子替换用临时文件。
        File.WriteAllText(temporary, json);
        File.Move(temporary, file, true);
    }
}
