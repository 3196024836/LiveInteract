using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveInteract.Contracts;

/// <summary>一条没有背景的直播提示。</summary>
public sealed class FadingNotice
{
    public string Triggerer { get; set; } = "";
    public string Effect { get; set; } = "";
    public bool Success { get; set; }
    public double At { get; set; }
    public double Duration { get; set; }
    public double Alpha { get; set; }
}

/// <summary>最近提示按触发顺序向上排列，空闲时完全不绘制状态面板。</summary>
public sealed class FadingNoticeQueue
{
    private readonly List<FadingNotice> items = new();
    public void Add(double now, string triggerer, string effect, bool success, double seconds = 5)
    {
        items.Add(new() { At = now, Triggerer = DisplayNames.Clean(triggerer), Effect = effect, Success = success, Duration = Math.Max(1, seconds) });
        if (items.Count > 8) { items.RemoveAt(0); }
    }
    public IReadOnlyList<FadingNotice> Read(double now)
    {
        items.RemoveAll(item => now >= item.At + item.Duration);
        foreach (var item in items) { item.Alpha = Math.Max(0, Math.Min(1, (item.At + item.Duration - now) / Math.Min(1.5, item.Duration))); }
        return items.ToArray();
    }
}

/// <summary>昵称只作纯文本，不插入富文本标签，保留原名字的完整文字元素。</summary>
public static class DisplayNames
{
    public static string Clean(string name)
    {
        var plain = new string((name ?? "").Where(character => !char.IsControl(character)).ToArray()).Trim();
        return plain.Length == 0 ? "观众" : plain;
    }
}

/// <summary>控制台、原生提示与怪物归属使用相同的参数说明。</summary>
public static class ActionPresentation
{
    public static string Describe(ActionRequest request)
    {
        var descriptor = ActionCatalog.All.FirstOrDefault(a => a.Id == request.ActionId);
        if (descriptor == null) { return request.ActionId; }
        var suffix = descriptor.IsTemporary ? " · " + request.DurationSeconds + "秒" : "";
        var name = descriptor.Name;
        if (request.ActionId is "enemy.growStack" or "enemy.shrinkStack") { return (request.ActionId == "enemy.growStack" ? "变大" : "变小") + " · " + request.DurationSeconds + "秒（独立叠加）"; }
        if (request.ActionId.StartsWith("pickup.", StringComparison.Ordinal)) { return name + " +" + (request.Arguments.TryGetValue("value", out var amount) ? amount : "2") + "（拾取）"; }
        string Value(string key) => request.Arguments.TryGetValue(key, out var entered) ? entered : descriptor.Parameters.FirstOrDefault(p => p.Key == key)?.DefaultValue ?? "";
        if (descriptor.Parameters.Any(p => p.Key == "count")) { return name + " ×" + Value("count") + (request.ActionId.StartsWith("ally.", StringComparison.Ordinal) ? Value("lifetime") == "0" ? " · 持续跟随" : " · " + Value("lifetime") + "秒" : ""); }
        if (descriptor.Parameters.Any(p => p.Key == "value"))
        {
            if (name.EndsWith("倍率", StringComparison.Ordinal)) { return name.Substring(0, name.Length - 2) + " ×" + Value("value") + suffix; }
            return name + " " + Value("value") + (request.ActionId == "health.blue" ? "点" : request.ActionId.StartsWith("health.", StringComparison.Ordinal) ? "格" : request.ActionId.StartsWith("silk.", StringComparison.Ordinal) || request.ActionId == "boss.heal" || request.ActionId == "boss.damage" || request.ActionId == "enemy.heal" || request.ActionId == "enemy.damage" ? "点" : "") + suffix;
        }
        return name + suffix;
    }
}
