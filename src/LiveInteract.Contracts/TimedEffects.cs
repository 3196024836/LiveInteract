using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveInteract.Contracts;

/// <summary>可测试的临时数值容器，保留首个基础值并按真实时间恢复，不依赖 Unity。</summary>
public sealed class TimedEffects
{
    // 同一对象属性共享原始值，不重复捕获已叠加数值。
    private readonly Dictionary<string, Slot> slots = new();

    /// <summary>包含属性恢复时遇到的错误，调用方应记录而不是隐藏。</summary>
    public event Action<string>? RestoreFailed;

    /// <summary>添加或合并临时效果。</summary>
    /// <param name="key">属性与对象唯一键。</param>
    /// <param name="value">效果数值。</param>
    /// <param name="duration">持续秒数。</param>
    /// <param name="stacking">重入策略。</param>
    /// <param name="multiply">是否乘以基础值。</param>
    /// <param name="read">读取未加成值。</param>
    /// <param name="write">应用属性。</param>
    /// <param name="now">单调时间。</param>
    /// <param name="alive">目标是否仍有效。</param>
    public void Apply(string key, double value, double duration, EffectStacking stacking, bool multiply, Func<double> read, Action<double> write, double now, Func<bool>? alive = null)
    {
        if (!slots.TryGetValue(key, out var slot))
        {
            slot = new Slot(read(), write, alive ?? (() => true));
            slots.Add(key, slot);
        }
        slot.Items.RemoveAll(item => item.End <= now);
        var previous = slot.Items.Select(item => new Effect(item.Value, item.End, item.Multiply)).ToArray(); // 保存完整回滚状态，包括延长前的结束时刻。
        if (stacking == EffectStacking.Extend && slot.Items.Count > 0)
        {
            slot.Items[slot.Items.Count - 1].End += duration;
        }
        else
        {
            if (stacking == EffectStacking.Refresh)
            {
                slot.Items.Clear();
            }
            slot.Items.Add(new(value, now + duration, multiply));
        }
        var calculated = Calculate(slot); // 无效叠加不进入游戏物理属性。
        if (double.IsNaN(calculated) || double.IsInfinity(calculated)) { slot.Items.Clear(); slot.Items.AddRange(previous); throw new InvalidOperationException("叠加后的数值无效，未应用该效果；原有恢复记录已保留。"); }
        try { slot.Write(calculated); }
        catch { slot.Items.Clear(); slot.Items.AddRange(previous); throw; }
    }

    /// <summary>推进效果并恢复过期属性。</summary>
    /// <param name="now">单调时间。</param>
    public void Tick(double now)
    {
        foreach (var pair in slots.ToArray())
        {
            var slot = pair.Value; // 当前属性槽。
            slot.Items.RemoveAll(item => item.End <= now);
            if (!slot.Alive())
            {
                slots.Remove(pair.Key);
                continue;
            }
            WriteValue(slot, Calculate(slot));
            if (slot.Items.Count == 0)
            {
                slots.Remove(pair.Key);
            }
        }
    }

    /// <summary>取消全部临时效果并恢复仍有效的目标。</summary>
    public void RestoreAll()
    {
        foreach (var slot in slots.Values)
        {
            if (slot.Alive())
            {
                WriteValue(slot, slot.BaseValue);
            }
        }
        slots.Clear();
    }

    /// <summary>只恢复选定类别，用于清除敌人整活而不收回援军或主播增益。</summary>
    public void RestorePrefix(string prefix)
    {
        foreach (var key in slots.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            var slot = slots[key]; if (slot.Alive()) { WriteValue(slot, slot.BaseValue); } slots.Remove(key);
        }
    }

    /// <summary>读取效果和剩余秒数。</summary>
    /// <param name="now">单调时间。</param>
    /// <returns>显示列表。</returns>
    public IReadOnlyList<string> Describe(double now) => slots.Where(pair => pair.Value.Items.Count > 0).Select(pair => pair.Key + " · " + Math.Max(0, pair.Value.Items.Max(i => i.End) - now).ToString("0.0") + "s").ToArray();

    /// <summary>依序组合乘数和覆盖数值。</summary>
    /// <param name="slot">属性槽。</param>
    /// <returns>应应用的属性值。</returns>
    private static double Calculate(Slot slot)
    {
        var result = slot.BaseValue; // 未修改基础值。
        foreach (var item in slot.Items)
        {
            result = item.Multiply ? result * item.Value : item.Value;
        }
        return result;
    }

    /// <summary>写入一个属性；单个目标失败不阻止其余目标恢复。</summary>
    /// <param name="slot">属性槽。</param>
    /// <param name="value">新的属性值。</param>
    private void WriteValue(Slot slot, double value)
    {
        try
        {
            slot.Write(value);
        }
        catch (Exception exception)
        {
            RestoreFailed?.Invoke(exception.Message);
        }
    }

    /// <summary>记录属性的真实基础值及有效目标回调。</summary>
    private sealed class Slot
    {
        // 首次添加时捕获的基础值与操作回调。
        public readonly double BaseValue;
        public readonly Action<double> Write;
        public readonly Func<bool> Alive;
        public readonly List<Effect> Items = new();

        /// <summary>记录属性恢复所需的上下文。</summary>
        /// <param name="baseValue">原始值。</param>
        /// <param name="write">写入回调。</param>
        /// <param name="alive">目标有效性回调。</param>
        public Slot(double baseValue, Action<double> write, Func<bool> alive)
        {
            BaseValue = baseValue;
            Write = write;
            Alive = alive;
        }
    }

    /// <summary>单个尚未到期的属性修饰。</summary>
    private sealed class Effect
    {
        // 效果数值、结束时刻与组合方式。
        public readonly double Value;
        public double End;
        public readonly bool Multiply;

        /// <summary>建立可独立到期的效果。</summary>
        /// <param name="value">效果值。</param>
        /// <param name="end">结束时刻。</param>
        /// <param name="multiply">是否乘法组合。</param>
        public Effect(double value, double end, bool multiply)
        {
            Value = value;
            End = end;
            Multiply = multiply;
        }
    }
}
