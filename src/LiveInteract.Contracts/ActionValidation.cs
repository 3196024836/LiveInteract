using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveInteract.Contracts;

/// <summary>在控制台和游戏两端执行相同的指令参数与永久修改校验。</summary>
public static class ActionValidation
{
    /// <summary>校验请求并拒绝未知动作、非有限数值和未授权永久修改。</summary>
    /// <param name="request">准备执行的指令。</param>
    /// <returns>中文错误列表。</returns>
    public static IReadOnlyList<string> Validate(ActionRequest request)=>Validate(request,true);

    /// <summary>仅供配置读取做结构校验；执行与编辑保存必须使用 Validate，不覆盖或删除旧规则。</summary>
    public static IReadOnlyList<string> ValidateStored(ActionRequest request)=>Validate(request,false);

    private static IReadOnlyList<string> Validate(ActionRequest request,bool enforceSummonEligibility)
    {
        var errors = new List<string>(); // 当前校验错误。
        var descriptor = ActionCatalog.All.FirstOrDefault(a => a.Id == request.ActionId); // 固定动作定义。
        var direct=MonsterCatalog.Find(request.ActionId);
        if(direct==null&&NativeSummons.Find(request.ActionId) is {} ally) { direct=MonsterCatalog.Find(ally.MonsterId); }
        if(direct==null&&request.ActionId.StartsWith("ally.convert.",StringComparison.Ordinal)) { direct=MonsterCatalog.Find(request.ActionId.Substring("ally.convert.".Length)); }
        if(direct!=null&&enforceSummonEligibility)
        {
            var rejection=SummonEligibility.RejectionReason(direct);
            if(rejection.Length>0) { errors.Add(rejection);return errors; }
        }
        if (request.GameId != "silksong" || descriptor == null || descriptor.Support == "unavailable" && (enforceSummonEligibility || direct==null))
        {
            errors.Add("当前游戏不支持此动作：" + request.ActionId);
            return errors;
        }
        if (descriptor.IsPersistent && !request.AllowPersistent)
        {
            errors.Add("永久修改尚未由主播在本次会话中确认。");
        }
        if (double.IsNaN(request.DurationSeconds) || double.IsInfinity(request.DurationSeconds) ||
            request.DurationSeconds < 0 || request.DurationSeconds > 3600 || descriptor.IsTemporary && request.DurationSeconds < 0.1 && !(request.DurationSeconds == 0 && request.ActionId is "enemy.growStack" or "enemy.shrinkStack"))
        {
            errors.Add("临时效果时间应为 0.1 至 3600 秒。");
        }
        if (!Enum.IsDefined(typeof(EffectStacking), request.Stacking))
        {
            errors.Add("不支持的叠加方式。");
        }
        foreach (var unknown in request.Arguments.Keys.Where(k => descriptor.Parameters.All(p => p.Key != k)))
        {
            errors.Add("未知参数：" + unknown);
        }

        // 默认参数和界面输入使用同一套限制，不能由 IPC 绕过。
        foreach (var parameter in descriptor.Parameters)
        {
            var text = request.Arguments.TryGetValue(parameter.Key, out var entered) ? entered : parameter.DefaultValue; // 最终输入。
            if (text == null || text.Length > 512)
            {
                errors.Add(parameter.Label + "长度超出限制。");
                continue;
            }
            if (parameter.Kind is ParameterKind.Number or ParameterKind.Integer)
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    double.IsNaN(number) || double.IsInfinity(number) || number < parameter.Minimum || number > parameter.Maximum ||
                    parameter.Kind == ParameterKind.Integer && number != Math.Truncate(number))
                {
                    errors.Add(parameter.Label + "应处于 " + parameter.Minimum.ToString(CultureInfo.InvariantCulture) + " 至 " + parameter.Maximum.ToString(CultureInfo.InvariantCulture) + " 范围内。");
                }
            }
            else if (parameter.Kind == ParameterKind.Choice && !parameter.Choices.ContainsKey(text))
            {
                errors.Add(parameter.Label + "不是有效选项。");
            }
            else if (parameter.Kind == ParameterKind.Boolean && !bool.TryParse(text, out _))
            {
                errors.Add(parameter.Label + "必须是真或假。");
            }
            else if (parameter.Kind == ParameterKind.Text && string.IsNullOrWhiteSpace(text) &&
                (parameter.Key == "tool" || parameter.Key == "crest" || parameter.Key == "event" || parameter.Key == "scene" || parameter.Key == "asset"))
            {
                errors.Add("请填写" + parameter.Label + "。");
            }
        }
        if (request.ActionId is "enemy.spawn" or "enemy.asset" or "boss.asset" or "boss.clone")
        {
            var key=request.ActionId is "enemy.asset" or "boss.asset"?"asset":"enemy";
            var source=request.Arguments.TryGetValue(key,out var enteredSource)?enteredSource:"";
            var monster=SummonCommands.FindSource(source);
            if(monster!=null&&enforceSummonEligibility)
            {
                var rejection=SummonEligibility.RejectionReason(monster);
                if(rejection.Length>0) { errors.Add(rejection); }
            }
            else if(monster==null&&enforceSummonEligibility) { errors.Add("无法确认此旧入口的来源已获本轮批准；请使用已批准的准确怪物名，本次没有生成替代单位。"); }
        }
        if (request.ActionId is "summon.named" or "summon.named.ally" or "summon.named.enemy" &&
            request.Arguments.TryGetValue("name", out var name) && !string.IsNullOrWhiteSpace(name))
        {
            if(enforceSummonEligibility)
            {
                try { SummonCommands.Resolve(name); }
                catch (ArgumentException error) { errors.Add(error.Message); }
            }
        }
        if(enforceSummonEligibility)
        {
            try
            {
                if(request.ActionId is "summon.random" or "summon.random.ally" or "summon.random.enemy")
                    NativeSummons.RandomMonster("validation",request.Arguments.TryGetValue("pool",out var pool)?pool:"ordinary");
                else if(request.ActionId=="boss.random") { NativeSummons.Boss("validation"); }
                else if(request.ActionId=="ally.native.free-random") { NativeSummons.FreeAlly("validation"); }
                else if(request.ActionId=="enemy.random") { NativeSummons.RandomMonster("validation","ordinary"); }
                else if(request.ActionId is "enemy.clone" or "enemy.cloneSafe")
                    errors.Add("此旧场景复制入口无法确认来源已获本轮批准；请改用已批准具名召唤，本次没有生成替代单位。");
            }
            catch(Exception error) when(error is ArgumentException or InvalidOperationException) { errors.Add(error.Message); }
        }
        return errors;
    }

    /// <summary>根据动作目录补齐默认参数。</summary>
    /// <param name="request">即将执行的请求。</param>
    public static void FillDefaults(ActionRequest request)
    {
        foreach (var parameter in ActionCatalog.All.First(a => a.Id == request.ActionId).Parameters)
        {
            // 新增的可选字段不强加给旧调用方，避免旧 Mod 因未知参数拒绝原有命令。
            if(!request.Arguments.ContainsKey(parameter.Key) && (parameter.Key=="faction" || MonsterCatalog.Find(request.ActionId)!=null && parameter.Key is "power" or "lifetime"))continue;
            if (!request.Arguments.ContainsKey(parameter.Key))
            {
                request.Arguments[parameter.Key] = parameter.DefaultValue;
            }
        }
    }
}
