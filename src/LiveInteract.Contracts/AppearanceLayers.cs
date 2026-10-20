using System.Collections.Generic;
using System.Linq;
namespace LiveInteract.Contracts;
/// <summary>可逐条追溯和独立到期的外观叠层；线性增长避免指数变化破坏战斗。</summary>
public sealed class AppearanceLayer
{
    public string Who { get; set; } = "";
    public bool Grow { get; set; }
    public double Amount { get; set; }
    public double End { get; set; }
}
/// <summary>不限制层数，每次独立倒计时；变小分母始终大于零。</summary>
public sealed class AppearanceLayers
{
    private readonly List<AppearanceLayer> items = new();
    public IReadOnlyList<AppearanceLayer> Items => items;
    public int Count => items.Count;
    public double Multiplier => (1 + items.Where(i => i.Grow).Sum(i => i.Amount)) / (1 + items.Where(i => !i.Grow).Sum(i => i.Amount));
    public void Add(string who, bool grow, double amount, double seconds, double now) => items.Add(new() { Who=who, Grow=grow, Amount=amount, End=now+seconds });
    public void Tick(double now) => items.RemoveAll(i => i.End<=now);
    public void Clear() => items.Clear();
}
